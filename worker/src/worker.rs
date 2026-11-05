//! Main loop: claim a job, run it, record the result; scheduled maintenance.

use std::sync::Arc;
use std::time::Duration;

use serde_json::Value;
use tokio::sync::watch;
use tokio::time::Instant;
use tracing::{debug, error, info, warn};

use crate::arango::{ArangoClient, ArangoError};
use crate::config::Config;
use crate::email::{self, EmailError, Mailer, SendEmailPayload};
use crate::i18n::Catalog;
use crate::invitations;
use crate::jobs::{Job, JobQueue, JobStatus};
use crate::payment::{self, PaymentError, PaymentWebhookPayload, WebhookOutcome};

pub const SEND_EMAIL: &str = "send_email";
pub const PAYMENT_WEBHOOK: &str = "payment_webhook";

#[derive(Debug, thiserror::Error)]
pub enum JobError {
    #[error("unknown job type {0:?}")]
    UnknownType(String),
    #[error(transparent)]
    Email(#[from] EmailError),
    #[error(transparent)]
    Payment(#[from] PaymentError),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProcessedJob {
    pub key: String,
    pub kind: String,
    pub status: JobStatus,
}

#[derive(Debug, Clone)]
pub struct Settings {
    pub app_base_url: String,
    pub poll_interval: Duration,
    pub expire_invitations_every: Duration,
    pub stale_job_timeout: Duration,
}

impl From<&Config> for Settings {
    fn from(c: &Config) -> Self {
        Self {
            app_base_url: c.app_base_url.clone(),
            poll_interval: c.poll_interval,
            expire_invitations_every: c.expire_invitations_every,
            stale_job_timeout: c.stale_job_timeout,
        }
    }
}

#[derive(Clone)]
pub struct Worker {
    db: ArangoClient,
    queue: JobQueue,
    mailer: Mailer,
    catalog: Arc<Catalog>,
    settings: Settings,
}

impl Worker {
    pub fn new(db: ArangoClient, mailer: Mailer, catalog: Catalog, settings: Settings) -> Self {
        Self {
            queue: JobQueue::new(db.clone()),
            db,
            mailer,
            catalog: Arc::new(catalog),
            settings,
        }
    }

    /// Builds the worker from the configuration (texts are loaded from `I18N_DIR`).
    pub fn from_config(config: &Config) -> anyhow::Result<Self> {
        let db = ArangoClient::new(
            &config.arango_url,
            &config.arango_database,
            &config.arango_user,
            &config.arango_password,
        )?;
        let mailer = Mailer::new(&config.smtp_host, config.smtp_port, &config.mail_from)?;
        let catalog = Catalog::load_dir(&config.i18n_dir)?;
        Ok(Self::new(db, mailer, catalog, config.into()))
    }

    pub fn queue(&self) -> &JobQueue {
        &self.queue
    }

    /// Claims and runs at most one job. `Ok(None)` means the queue is empty.
    /// Errors are database errors around the job, never the job's own failure.
    pub async fn run_once(&self) -> Result<Option<ProcessedJob>, ArangoError> {
        let Some(job) = self.queue.claim().await? else {
            return Ok(None);
        };
        debug!(job = %job.key, kind = %job.kind, attempt = job.attempts, "job claimed");
        let status = match self.execute(&job).await {
            Ok(payload) => {
                self.queue.complete(&job, &payload).await?;
                info!(job = %job.key, kind = %job.kind, "job done");
                JobStatus::Done
            }
            Err(e) => {
                let status = self.queue.fail(&job, &e.to_string()).await?;
                match &status {
                    JobStatus::Failed => {
                        error!(job = %job.key, kind = %job.kind, error = %e, "job failed")
                    }
                    _ => {
                        warn!(job = %job.key, kind = %job.kind, error = %e, "job will be retried")
                    }
                }
                status
            }
        };
        Ok(Some(ProcessedJob {
            key: job.key,
            kind: job.kind,
            status,
        }))
    }

    /// Runs the job and returns the payload to store with the result.
    async fn execute(&self, job: &Job) -> Result<Value, JobError> {
        match job.kind.as_str() {
            SEND_EMAIL => {
                let payload = SendEmailPayload::parse(&job.payload)?;
                let rendered = email::render(&self.catalog, &payload)?;
                self.mailer.send(&rendered).await?;
                Ok(email::redact_link(&job.payload))
            }
            PAYMENT_WEBHOOK => {
                let payload = PaymentWebhookPayload::parse(&job.payload)?;
                match payment::process(&self.db, &payload, &self.settings.app_base_url).await? {
                    WebhookOutcome::Upgraded {
                        org_id,
                        email_job_id,
                    } => {
                        info!(org = %org_id, email_job = %email_job_id, "organization upgraded to Pro")
                    }
                    WebhookOutcome::AlreadyProcessed { status } => {
                        info!(payment = %payload.payment_id, %status, "payment already processed")
                    }
                }
                Ok(job.payload.clone())
            }
            other => Err(JobError::UnknownType(other.to_owned())),
        }
    }

    /// Scheduled work: release stale jobs, expire invitations. Errors are logged.
    pub async fn run_maintenance(&self) {
        match self
            .queue
            .reclaim_stale(self.settings.stale_job_timeout)
            .await
        {
            Ok(0) => {}
            Ok(n) => warn!(count = n, "stale running jobs released"),
            Err(e) => warn!(error = %e, "cannot release stale jobs"),
        }
        match invitations::expire(&self.db).await {
            Ok(0) => {}
            Ok(n) => info!(count = n, "invitations expired"),
            Err(e) => warn!(error = %e, "cannot expire invitations"),
        }
    }

    /// Runs until `shutdown` becomes `true` (or its sender is dropped).
    /// A job in progress is always finished before returning.
    pub async fn run(&self, mut shutdown: watch::Receiver<bool>) {
        info!("worker started");
        let mut next_maintenance = Instant::now();
        while !*shutdown.borrow() {
            if Instant::now() >= next_maintenance {
                self.run_maintenance().await;
                next_maintenance = Instant::now() + self.settings.expire_invitations_every;
            }
            let busy = match self.run_once().await {
                Ok(processed) => processed.is_some(),
                Err(e) => {
                    warn!(error = %e, "job queue unavailable");
                    false
                }
            };
            if busy {
                continue;
            }
            let idle = self
                .settings
                .poll_interval
                .min(next_maintenance.saturating_duration_since(Instant::now()));
            tokio::select! {
                () = tokio::time::sleep(idle) => {}
                changed = shutdown.changed() => {
                    if changed.is_err() {
                        break;
                    }
                }
            }
        }
        info!("worker stopped");
    }
}
