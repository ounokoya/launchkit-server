//! Job queue stored in the `jobs` collection (ADR-0005): atomic claim,
//! completion, retry with exponential backoff, stale lock recovery.

use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{Value, json};

use crate::arango::{ArangoClient, ArangoError, retry_on_conflict};
use crate::clock;

/// `jobs.maxAttempts` default from the data contract, used when a job has none
/// and for the jobs the worker creates itself.
pub const DEFAULT_MAX_ATTEMPTS: u32 = 5;
/// Claims retried on write-write conflict before giving up for this round.
const CLAIM_TRIES: u32 = 10;
/// `lastError` is truncated to this many characters.
const MAX_ERROR_LEN: usize = 2000;

/// Picks the oldest due `pending` job and marks it `running` in one statement.
/// `ignoreRevs: false` makes the update a compare-and-swap on `_rev`: when two
/// workers race for the same job, one gets error 1200 and claims again.
pub const CLAIM_QUERY: &str = r#"
FOR j IN jobs
  FILTER j.status == "pending" AND j.runAt <= @now
  SORT j.runAt ASC, j.createdAt ASC
  LIMIT 1
  UPDATE j WITH { status: "running", attempts: j.attempts + 1, lockedAt: @now } IN jobs
    OPTIONS { ignoreRevs: false }
  RETURN NEW
"#;

/// Applies `@patch` to a job only if it is still the run we claimed.
/// `mergeObjects: false` lets `payload` be replaced as a whole (link redaction).
pub const FINISH_QUERY: &str = r#"
FOR j IN jobs
  FILTER j._key == @key AND j.status == "running" AND j.lockedAt == @lockedAt
  UPDATE j WITH @patch IN jobs OPTIONS { mergeObjects: false }
  RETURN NEW._key
"#;

/// Jobs left `running` by a stopped worker go back to `pending`, or to
/// `failed` when they have no attempt left.
pub const RECLAIM_QUERY: &str = r#"
FOR j IN jobs
  FILTER j.status == "running" AND j.lockedAt <= @cutoff
  LET exhausted = j.attempts >= NOT_NULL(j.maxAttempts, @defaultMaxAttempts)
  LET link = exhausted AND j.payload.params.link != null ? { params: { link: @droppedLink } } : {}
  UPDATE j WITH (exhausted
    ? { status: "failed", finishedAt: @now, lastError: @reason, payload: link }
    : { status: "pending", lockedAt: null, lastError: @reason }) IN jobs
  RETURN NEW.status
"#;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Job {
    #[serde(rename = "_key")]
    pub key: String,
    #[serde(rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub payload: Value,
    #[serde(default)]
    pub attempts: u32,
    #[serde(default = "default_max_attempts")]
    pub max_attempts: u32,
    #[serde(default)]
    pub locked_at: Option<String>,
}

fn default_max_attempts() -> u32 {
    DEFAULT_MAX_ATTEMPTS
}

/// Delay before the next try: `2^attempts` seconds (2, 4, 8, 16 s).
pub fn backoff(attempts: u32) -> Duration {
    Duration::from_secs(1u64 << attempts.min(20))
}

/// What happens to a job whose run failed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum FailureDecision {
    /// Back to `pending`, not before `run_at`.
    Retry { run_at: String },
    /// No attempt left: `failed`.
    Fail,
}

pub fn decide_failure(attempts: u32, max_attempts: u32, now: DateTime<Utc>) -> FailureDecision {
    if attempts < max_attempts {
        FailureDecision::Retry {
            run_at: clock::iso(clock::add(now, backoff(attempts))),
        }
    } else {
        FailureDecision::Fail
    }
}

fn truncate(message: &str) -> String {
    message.chars().take(MAX_ERROR_LEN).collect()
}

/// Final state of a processed job.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum JobStatus {
    Done,
    Retry { run_at: String },
    Failed,
}

#[derive(Clone)]
pub struct JobQueue {
    db: ArangoClient,
}

impl JobQueue {
    pub fn new(db: ArangoClient) -> Self {
        Self { db }
    }

    /// Claims the next due job, if any.
    pub async fn claim(&self) -> Result<Option<Job>, ArangoError> {
        retry_on_conflict(CLAIM_TRIES, || async {
            self.db
                .query_first(CLAIM_QUERY, json!({ "now": clock::iso(clock::now()) }))
                .await
        })
        .await
    }

    /// Marks the job `done`, storing `payload` (possibly redacted).
    pub async fn complete(&self, job: &Job, payload: &Value) -> Result<bool, ArangoError> {
        let patch = json!({
            "status": "done",
            "finishedAt": clock::iso(clock::now()),
            "lastError": null,
            "payload": payload,
        });
        self.finish(job, patch).await
    }

    /// Records a failed run: retry later or mark `failed`.
    pub async fn fail(&self, job: &Job, error: &str) -> Result<JobStatus, ArangoError> {
        let now = clock::now();
        let (patch, status) = match decide_failure(job.attempts, job.max_attempts, now) {
            FailureDecision::Retry { run_at } => (
                json!({
                    "status": "pending",
                    "runAt": run_at,
                    "lockedAt": null,
                    "lastError": truncate(error),
                }),
                JobStatus::Retry { run_at },
            ),
            // A job that ends `failed` must not keep a clear token in its link either.
            FailureDecision::Fail => (
                json!({
                    "status": "failed",
                    "finishedAt": clock::iso(now),
                    "lastError": truncate(error),
                    "payload": crate::email::replace_link(&job.payload, crate::email::DROPPED_LINK),
                }),
                JobStatus::Failed,
            ),
        };
        self.finish(job, patch).await?;
        Ok(status)
    }

    /// Returns `false` when the job was no longer ours (reclaimed meanwhile).
    async fn finish(&self, job: &Job, patch: Value) -> Result<bool, ArangoError> {
        let vars = json!({ "key": job.key, "lockedAt": job.locked_at, "patch": patch });
        let updated: Vec<String> = retry_on_conflict(CLAIM_TRIES, || async {
            self.db.query(FINISH_QUERY, vars.clone()).await
        })
        .await?;
        if updated.is_empty() {
            tracing::warn!(job = %job.key, "job was reclaimed before it finished; result not saved");
        }
        Ok(!updated.is_empty())
    }

    /// Releases jobs locked for longer than `timeout`. Returns how many.
    pub async fn reclaim_stale(&self, timeout: Duration) -> Result<usize, ArangoError> {
        let now = clock::now();
        let vars = json!({
            "cutoff": clock::iso(clock::sub(now, timeout)),
            "now": clock::iso(now),
            "defaultMaxAttempts": DEFAULT_MAX_ATTEMPTS,
            "droppedLink": crate::email::DROPPED_LINK,
            "reason": format!("worker stopped while running the job (locked for more than {} s)", timeout.as_secs()),
        });
        let statuses: Vec<String> = retry_on_conflict(CLAIM_TRIES, || async {
            self.db.query(RECLAIM_QUERY, vars.clone()).await
        })
        .await?;
        Ok(statuses.len())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn backoff_doubles_from_two_seconds() {
        let delays: Vec<u64> = (1..=4).map(|a| backoff(a).as_secs()).collect();
        assert_eq!(delays, vec![2, 4, 8, 16]);
    }

    #[test]
    fn backoff_is_capped() {
        assert_eq!(backoff(64), backoff(20));
    }

    #[test]
    fn failure_retries_until_max_attempts() {
        let now = Utc.with_ymd_and_hms(2026, 11, 4, 9, 30, 0).unwrap();
        assert_eq!(
            decide_failure(1, 5, now),
            FailureDecision::Retry {
                run_at: "2026-11-04T09:30:02.000Z".into()
            }
        );
        assert_eq!(
            decide_failure(4, 5, now),
            FailureDecision::Retry {
                run_at: "2026-11-04T09:30:16.000Z".into()
            }
        );
        assert_eq!(decide_failure(5, 5, now), FailureDecision::Fail);
        assert_eq!(decide_failure(6, 5, now), FailureDecision::Fail);
    }

    #[test]
    fn parses_claimed_job_with_defaults() {
        let job: Job = serde_json::from_value(json!({
            "_key": "42", "_id": "jobs/42", "_rev": "x", "type": "send_email",
            "payload": {"to": "a@b.c"}, "status": "running", "attempts": 1,
            "runAt": "2026-11-04T09:30:00.000Z", "lockedAt": "2026-11-04T09:30:00.000Z"
        }))
        .unwrap();
        assert_eq!(job.key, "42");
        assert_eq!(job.kind, "send_email");
        assert_eq!(job.attempts, 1);
        assert_eq!(job.max_attempts, DEFAULT_MAX_ATTEMPTS);
        assert_eq!(job.locked_at.as_deref(), Some("2026-11-04T09:30:00.000Z"));
    }

    #[test]
    fn truncates_long_errors() {
        let long = "é".repeat(MAX_ERROR_LEN + 10);
        assert_eq!(truncate(&long).chars().count(), MAX_ERROR_LEN);
        assert_eq!(truncate("short"), "short");
    }
}
