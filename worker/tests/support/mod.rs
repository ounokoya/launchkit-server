//! Test environment: ArangoDB and Mailpit containers (random host ports),
//! one database per scenario with the collections of `server-donnees.md`.

use std::collections::HashMap;
use std::path::PathBuf;
use std::time::Duration;

use anyhow::{Context, bail};
use launchkit_worker::arango::{ArangoClient, CollectionKind};
use launchkit_worker::{Config, Worker, clock};
use serde_json::{Value, json};
use testcontainers::core::{IntoContainerPort, WaitFor};
use testcontainers::runners::AsyncRunner;
use testcontainers::{ContainerAsync, GenericImage, ImageExt};

pub const ARANGO_PASSWORD: &str = "launchkit-test";
pub const APP_BASE_URL: &str = "http://localhost:5173";

pub struct Containers {
    arango: ContainerAsync<GenericImage>,
    mailpit: ContainerAsync<GenericImage>,
    pub env: Env,
}

/// Endpoints of the running containers.
#[derive(Clone)]
pub struct Env {
    pub arango_url: String,
    pub smtp_host: String,
    pub smtp_port: u16,
    pub mailpit_url: String,
    http: reqwest::Client,
}

impl Containers {
    pub async fn start() -> anyhow::Result<Self> {
        let arango = GenericImage::new("arangodb", "3.12")
            .with_exposed_port(8529.tcp())
            .with_wait_for(WaitFor::message_on_either_std("is ready for business"))
            .with_env_var("ARANGO_ROOT_PASSWORD", ARANGO_PASSWORD)
            .with_startup_timeout(Duration::from_secs(180))
            .start();
        let mailpit = GenericImage::new("axllent/mailpit", "latest")
            .with_exposed_port(1025.tcp())
            .with_exposed_port(8025.tcp())
            .with_wait_for(WaitFor::message_on_either_std("accessible via"))
            .with_startup_timeout(Duration::from_secs(60))
            .start();
        let (arango, mailpit) = tokio::join!(arango, mailpit);
        let (arango, mailpit) = (
            arango.context("start arangodb:3.12")?,
            mailpit.context("start axllent/mailpit")?,
        );

        let arango_host = arango.get_host().await?;
        let arango_port = arango.get_host_port_ipv4(8529).await?;
        let mail_host = mailpit.get_host().await?;
        let env = Env {
            arango_url: format!("http://{arango_host}:{arango_port}"),
            smtp_host: mail_host.to_string(),
            smtp_port: mailpit.get_host_port_ipv4(1025).await?,
            mailpit_url: format!(
                "http://{mail_host}:{}",
                mailpit.get_host_port_ipv4(8025).await?
            ),
            http: reqwest::Client::new(),
        };
        env.wait_until_ready().await?;
        Ok(Self {
            arango,
            mailpit,
            env,
        })
    }

    pub async fn stop(self) {
        let _ = self.arango.rm().await;
        let _ = self.mailpit.rm().await;
    }
}

impl Env {
    /// The ArangoDB entrypoint restarts the server after init: poll until the
    /// real one answers, and until Mailpit's API answers.
    async fn wait_until_ready(&self) -> anyhow::Result<()> {
        let admin = self.client("_system");
        let deadline = tokio::time::Instant::now() + Duration::from_secs(90);
        loop {
            let arango_ok = admin.version().await.is_ok();
            let mailpit_ok = self
                .http
                .get(format!("{}/api/v1/messages", self.mailpit_url))
                .send()
                .await
                .is_ok_and(|r| r.status().is_success());
            if arango_ok && mailpit_ok {
                return Ok(());
            }
            if tokio::time::Instant::now() > deadline {
                bail!("containers not ready (arangodb: {arango_ok}, mailpit: {mailpit_ok})");
            }
            tokio::time::sleep(Duration::from_millis(250)).await;
        }
    }

    pub fn client(&self, database: &str) -> ArangoClient {
        ArangoClient::new(&self.arango_url, database, "root", ARANGO_PASSWORD).unwrap()
    }

    /// Fresh database with every collection and index of the data contract.
    pub async fn database(&self, name: &str) -> ArangoClient {
        let db = self.client(name);
        assert!(
            db.create_database().await.unwrap(),
            "database {name} exists"
        );
        create_schema(&db).await;
        db
    }

    pub fn config(&self, database: &str, smtp_port: u16) -> Config {
        let vars: HashMap<&str, String> = HashMap::from([
            ("ARANGO_URL", self.arango_url.clone()),
            ("ARANGO_DATABASE", database.to_owned()),
            ("ARANGO_USER", "root".to_owned()),
            ("ARANGO_PASSWORD", ARANGO_PASSWORD.to_owned()),
            ("SMTP_HOST", self.smtp_host.clone()),
            ("SMTP_PORT", smtp_port.to_string()),
            ("MAIL_FROM", "LaunchKit <noreply@demo.test>".to_owned()),
            ("APP_BASE_URL", format!("{APP_BASE_URL}/")),
            ("I18N_DIR", i18n_dir().display().to_string()),
            ("POLL_INTERVAL_MS", "50".to_owned()),
            ("EXPIRE_INVITATIONS_EVERY_SECS", "1".to_owned()),
            ("STALE_JOB_TIMEOUT_SECS", "300".to_owned()),
        ]);
        Config::from_lookup(|name| vars.get(name).cloned()).unwrap()
    }

    pub fn worker(&self, db: &ArangoClient) -> Worker {
        self.worker_with_smtp_port(db, self.smtp_port)
    }

    pub fn worker_with_smtp_port(&self, db: &ArangoClient, smtp_port: u16) -> Worker {
        Worker::from_config(&self.config(db.database(), smtp_port)).unwrap()
    }

    /// Summaries of the Mailpit messages sent to `address`.
    pub async fn messages_to(&self, address: &str) -> Vec<Value> {
        let list: Value = self
            .http
            .get(format!("{}/api/v1/messages?limit=500", self.mailpit_url))
            .send()
            .await
            .unwrap()
            .json()
            .await
            .unwrap();
        list["messages"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|m| {
                m["To"]
                    .as_array()
                    .is_some_and(|to| to.iter().any(|t| t["Address"] == address))
            })
            .cloned()
            .collect()
    }

    /// Full message (HTML, text, inline parts) by Mailpit ID.
    pub async fn message(&self, id: &str) -> Value {
        self.http
            .get(format!("{}/api/v1/message/{id}", self.mailpit_url))
            .send()
            .await
            .unwrap()
            .json()
            .await
            .unwrap()
    }

    /// Waits until exactly one message reached `address` and returns it in full.
    pub async fn single_message_to(&self, address: &str) -> Value {
        let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
        loop {
            let messages = self.messages_to(address).await;
            if !messages.is_empty() {
                assert_eq!(messages.len(), 1, "one e-mail expected for {address}");
                return self.message(messages[0]["ID"].as_str().unwrap()).await;
            }
            assert!(
                tokio::time::Instant::now() < deadline,
                "no e-mail for {address}"
            );
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    }
}

pub fn i18n_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../shared/i18n")
}

async fn create_schema(db: &ArangoClient) {
    use CollectionKind::{Document, Edge};
    for (name, kind) in [
        ("users", Document),
        ("organizations", Document),
        ("memberships", Edge),
        ("invitations", Document),
        ("projects", Document),
        ("activities", Document),
        ("jobs", Document),
        ("payments", Document),
        ("sessions", Document),
        ("email_tokens", Document),
    ] {
        db.create_collection(name, kind).await.unwrap();
    }
    for (collection, fields, unique) in [
        ("users", &["email"][..], true),
        ("memberships", &["_from", "_to"][..], true),
        ("invitations", &["orgId", "status"][..], false),
        ("invitations", &["status", "expiresAt"][..], false),
        ("invitations", &["tokenHash"][..], true),
        ("projects", &["orgId", "updatedAt"][..], false),
        ("activities", &["orgId", "createdAt"][..], false),
        ("activities", &["projectId", "createdAt"][..], false),
        ("jobs", &["status", "runAt"][..], false),
        ("sessions", &["tokenHash"][..], true),
        ("email_tokens", &["tokenHash"][..], true),
    ] {
        db.ensure_persistent_index(collection, fields, unique)
            .await
            .unwrap();
    }
    for collection in ["sessions", "email_tokens"] {
        db.ensure_ttl_index(collection, "expiresAt").await.unwrap();
    }
}

/// Inserts a document and returns its `_key`.
pub async fn insert(db: &ArangoClient, collection: &str, doc: Value) -> String {
    db.query_first::<String>(
        "INSERT @doc INTO @@collection RETURN NEW._key",
        json!({ "doc": doc, "@collection": collection }),
    )
    .await
    .unwrap()
    .unwrap()
}

pub async fn get(db: &ArangoClient, collection: &str, key: &str) -> Value {
    db.query_first::<Value>(
        "RETURN DOCUMENT(@@collection, @key)",
        json!({ "@collection": collection, "key": key }),
    )
    .await
    .unwrap()
    .unwrap()
}

pub async fn all(db: &ArangoClient, collection: &str) -> Vec<Value> {
    db.query(
        "FOR d IN @@collection SORT d._key RETURN d",
        json!({ "@collection": collection }),
    )
    .await
    .unwrap()
}

/// Makes a job due now (skips its backoff delay).
pub async fn make_due(db: &ArangoClient, key: &str) {
    db.query::<Value>(
        "UPDATE @key WITH { runAt: @now } IN jobs",
        json!({ "key": key, "now": clock::iso(clock::now()) }),
    )
    .await
    .unwrap();
}

/// A new `pending` job, due now, as the API would queue it.
pub fn job(kind: &str, payload: Value) -> Value {
    let now = clock::iso(clock::now());
    json!({
        "type": kind, "payload": payload, "status": "pending",
        "attempts": 0, "maxAttempts": 5, "runAt": now, "lockedAt": null,
        "lastError": null, "createdAt": now, "finishedAt": null
    })
}

/// A local port with nothing listening on it.
pub fn closed_port() -> u16 {
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.local_addr().unwrap().port()
}

/// Raw HTTP access for things the worker client does not do (stream transactions).
pub fn http() -> reqwest::Client {
    reqwest::Client::new()
}
