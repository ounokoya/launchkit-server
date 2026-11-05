//! Minimal ArangoDB client over the HTTP API (ADR-0008): AQL cursors with bind
//! variables, plus the few admin calls needed to create a database in tests.

use std::future::Future;
use std::time::Duration;

use reqwest::{Method, StatusCode, Url};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

/// Write-write conflict (`TRI_ERROR_ARANGO_CONFLICT`): safe to retry.
pub const ERROR_CONFLICT: i64 = 1200;
/// Unique constraint violated.
pub const ERROR_UNIQUE_CONSTRAINT: i64 = 1210;
/// Database or collection name already in use.
pub const ERROR_DUPLICATE_NAME: i64 = 1207;

const REQUEST_TIMEOUT: Duration = Duration::from_secs(30);
const CURSOR_BATCH_SIZE: u32 = 1000;

#[derive(Debug, thiserror::Error)]
pub enum ArangoError {
    #[error("write-write conflict: {0}")]
    Conflict(String),
    #[error("ArangoDB error {num} (HTTP {status}): {message}")]
    Server {
        status: u16,
        num: i64,
        message: String,
    },
    #[error("ArangoDB request failed: {0}")]
    Transport(#[from] reqwest::Error),
    #[error("invalid ArangoDB URL: {0}")]
    InvalidUrl(String),
    #[error("unexpected ArangoDB response: {0}")]
    Decode(String),
}

impl ArangoError {
    /// True for errors that a plain retry can fix (write-write conflict).
    pub fn is_conflict(&self) -> bool {
        matches!(self, Self::Conflict(_))
    }

    /// ArangoDB `errorNum`, when the server answered with one.
    pub fn error_num(&self) -> Option<i64> {
        match self {
            Self::Conflict(_) => Some(ERROR_CONFLICT),
            Self::Server { num, .. } => Some(*num),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CollectionKind {
    Document,
    Edge,
}

#[derive(Clone)]
pub struct ArangoClient {
    http: reqwest::Client,
    base_url: Url,
    database: String,
    user: String,
    password: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CursorRequest<'a> {
    query: &'a str,
    bind_vars: &'a Value,
    batch_size: u32,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct CursorResponse<T> {
    #[serde(default = "Vec::new")]
    result: Vec<T>,
    #[serde(default)]
    has_more: bool,
    id: Option<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct ErrorBody {
    #[serde(default)]
    error_num: i64,
    #[serde(default)]
    error_message: String,
}

impl ArangoClient {
    pub fn new(
        base_url: &str,
        database: &str,
        user: &str,
        password: &str,
    ) -> Result<Self, ArangoError> {
        let base_url = Url::parse(base_url)
            .ok()
            .filter(|u| !u.cannot_be_a_base())
            .ok_or_else(|| ArangoError::InvalidUrl(base_url.to_owned()))?;
        let http = reqwest::Client::builder()
            .timeout(REQUEST_TIMEOUT)
            .build()?;
        Ok(Self {
            http,
            base_url,
            database: database.to_owned(),
            user: user.to_owned(),
            password: password.to_owned(),
        })
    }

    pub fn database(&self) -> &str {
        &self.database
    }

    /// Same server and credentials, another database.
    pub fn with_database(&self, database: &str) -> Self {
        Self {
            database: database.to_owned(),
            ..self.clone()
        }
    }

    /// Runs a parameterised AQL query and returns every result, following
    /// `hasMore` across batches.
    pub async fn query<T: DeserializeOwned>(
        &self,
        aql: &str,
        bind_vars: Value,
    ) -> Result<Vec<T>, ArangoError> {
        let body = CursorRequest {
            query: aql,
            bind_vars: &bind_vars,
            batch_size: CURSOR_BATCH_SIZE,
        };
        let mut page: CursorResponse<T> = self
            .call(
                Method::POST,
                &self.database,
                &["_api", "cursor"],
                Some(&body),
            )
            .await?;
        let mut results = std::mem::take(&mut page.result);
        while page.has_more {
            let id = page
                .id
                .take()
                .ok_or_else(|| ArangoError::Decode("cursor has more results but no id".into()))?;
            page = self
                .call::<_, ()>(Method::POST, &self.database, &["_api", "cursor", &id], None)
                .await?;
            results.append(&mut page.result);
            page.id = page.id.or(Some(id));
        }
        Ok(results)
    }

    /// First result of a query, if any.
    pub async fn query_first<T: DeserializeOwned>(
        &self,
        aql: &str,
        bind_vars: Value,
    ) -> Result<Option<T>, ArangoError> {
        Ok(self.query(aql, bind_vars).await?.into_iter().next())
    }

    /// Server version string (`GET /_api/version`); also a cheap health check.
    pub async fn version(&self) -> Result<String, ArangoError> {
        let body: Value = self
            .call::<_, ()>(Method::GET, "_system", &["_api", "version"], None)
            .await?;
        body.get("version")
            .and_then(Value::as_str)
            .map(str::to_owned)
            .ok_or_else(|| ArangoError::Decode("version missing".into()))
    }

    /// Creates the client's database. Returns `false` if it already existed.
    pub async fn create_database(&self) -> Result<bool, ArangoError> {
        let body = json!({ "name": self.database });
        ignore_duplicate(
            self.call::<_, Value>(Method::POST, "_system", &["_api", "database"], Some(&body))
                .await,
        )
    }

    /// Creates a collection. Returns `false` if it already existed.
    pub async fn create_collection(
        &self,
        name: &str,
        kind: CollectionKind,
    ) -> Result<bool, ArangoError> {
        let kind = match kind {
            CollectionKind::Document => 2,
            CollectionKind::Edge => 3,
        };
        let body = json!({ "name": name, "type": kind });
        ignore_duplicate(
            self.call::<_, Value>(
                Method::POST,
                &self.database,
                &["_api", "collection"],
                Some(&body),
            )
            .await,
        )
    }

    /// Ensures a persistent index (idempotent on the server side).
    pub async fn ensure_persistent_index(
        &self,
        collection: &str,
        fields: &[&str],
        unique: bool,
    ) -> Result<(), ArangoError> {
        let body = json!({ "type": "persistent", "fields": fields, "unique": unique });
        self.ensure_index(collection, &body).await
    }

    /// Ensures a TTL index: documents are removed once `field` is in the past.
    pub async fn ensure_ttl_index(&self, collection: &str, field: &str) -> Result<(), ArangoError> {
        let body = json!({ "type": "ttl", "fields": [field], "expireAfter": 0 });
        self.ensure_index(collection, &body).await
    }

    async fn ensure_index(&self, collection: &str, body: &Value) -> Result<(), ArangoError> {
        let mut url = self.url(&self.database, &["_api", "index"])?;
        url.query_pairs_mut().append_pair("collection", collection);
        let _: Value = self.send(Method::POST, url, Some(body)).await?;
        Ok(())
    }

    fn url(&self, database: &str, segments: &[&str]) -> Result<Url, ArangoError> {
        let mut url = self.base_url.clone();
        url.path_segments_mut()
            .map_err(|_| ArangoError::InvalidUrl(self.base_url.to_string()))?
            .pop_if_empty()
            .push("_db")
            .push(database)
            .extend(segments);
        Ok(url)
    }

    async fn call<T: DeserializeOwned, B: Serialize>(
        &self,
        method: Method,
        database: &str,
        segments: &[&str],
        body: Option<&B>,
    ) -> Result<T, ArangoError> {
        let url = self.url(database, segments)?;
        self.send(method, url, body).await
    }

    async fn send<T: DeserializeOwned, B: Serialize>(
        &self,
        method: Method,
        url: Url,
        body: Option<&B>,
    ) -> Result<T, ArangoError> {
        let mut request = self
            .http
            .request(method, url)
            .basic_auth(&self.user, Some(&self.password));
        if let Some(body) = body {
            request = request.json(body);
        }
        let response = request.send().await?;
        let status = response.status();
        let bytes = response.bytes().await?;
        if status.is_success() {
            serde_json::from_slice(&bytes).map_err(|e| ArangoError::Decode(e.to_string()))
        } else {
            Err(error_from_response(status, &bytes))
        }
    }
}

fn error_from_response(status: StatusCode, bytes: &[u8]) -> ArangoError {
    match serde_json::from_slice::<ErrorBody>(bytes) {
        Ok(body) if body.error_num == ERROR_CONFLICT => ArangoError::Conflict(body.error_message),
        Ok(body) => ArangoError::Server {
            status: status.as_u16(),
            num: body.error_num,
            message: body.error_message,
        },
        Err(_) => ArangoError::Server {
            status: status.as_u16(),
            num: 0,
            message: String::from_utf8_lossy(bytes).chars().take(500).collect(),
        },
    }
}

fn ignore_duplicate(result: Result<Value, ArangoError>) -> Result<bool, ArangoError> {
    match result {
        Ok(_) => Ok(true),
        Err(e) if e.error_num() == Some(ERROR_DUPLICATE_NAME) => Ok(false),
        Err(e) => Err(e),
    }
}

/// Runs `operation`, retrying on write-write conflicts with a short linear pause.
/// Other errors, and the last conflict, are returned as is.
pub async fn retry_on_conflict<T, F, Fut>(
    max_tries: u32,
    mut operation: F,
) -> Result<T, ArangoError>
where
    F: FnMut() -> Fut,
    Fut: Future<Output = Result<T, ArangoError>>,
{
    let mut tries = 0;
    loop {
        tries += 1;
        match operation().await {
            Err(e) if e.is_conflict() && tries < max_tries => {
                tracing::debug!(tries, "write-write conflict, retrying");
                tokio::time::sleep(Duration::from_millis(10 * u64::from(tries))).await;
            }
            other => return other,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    #[test]
    fn maps_conflict_error_num() {
        let err = error_from_response(
            StatusCode::CONFLICT,
            br#"{"error":true,"code":409,"errorNum":1200,"errorMessage":"write-write conflict"}"#,
        );
        assert!(err.is_conflict());
        assert_eq!(err.error_num(), Some(ERROR_CONFLICT));
    }

    #[test]
    fn maps_other_server_errors() {
        let err = error_from_response(
            StatusCode::CONFLICT,
            br#"{"error":true,"code":409,"errorNum":1210,"errorMessage":"unique constraint violated"}"#,
        );
        assert!(!err.is_conflict());
        assert_eq!(err.error_num(), Some(ERROR_UNIQUE_CONSTRAINT));
        assert!(err.to_string().contains("unique constraint violated"));
    }

    #[test]
    fn keeps_non_json_error_bodies() {
        let err = error_from_response(StatusCode::BAD_GATEWAY, b"bad gateway");
        match err {
            ArangoError::Server {
                status, message, ..
            } => {
                assert_eq!(status, 502);
                assert_eq!(message, "bad gateway");
            }
            other => panic!("unexpected {other:?}"),
        }
    }

    #[test]
    fn builds_encoded_urls() {
        let client = ArangoClient::new("http://db:8529/", "launch kit", "root", "").unwrap();
        let url = client.url(client.database(), &["_api", "cursor"]).unwrap();
        assert_eq!(url.as_str(), "http://db:8529/_db/launch%20kit/_api/cursor");
        let other = client.with_database("other");
        assert_eq!(other.database(), "other");
    }

    #[test]
    fn rejects_invalid_base_url() {
        assert!(matches!(
            ArangoClient::new("not a url", "db", "root", ""),
            Err(ArangoError::InvalidUrl(_))
        ));
        assert!(matches!(
            ArangoClient::new("mailto:x@y.z", "db", "root", ""),
            Err(ArangoError::InvalidUrl(_))
        ));
    }

    #[test]
    fn duplicate_names_are_not_errors() {
        let dup = ArangoError::Server {
            status: 409,
            num: ERROR_DUPLICATE_NAME,
            message: "duplicate name".into(),
        };
        assert!(!ignore_duplicate(Err(dup)).unwrap());
        assert!(ignore_duplicate(Ok(Value::Null)).unwrap());
        let other = ArangoError::Decode("x".into());
        assert!(ignore_duplicate(Err(other)).is_err());
    }

    #[tokio::test]
    async fn retries_conflicts_only() {
        let calls = AtomicU32::new(0);
        let result = retry_on_conflict(5, || async {
            if calls.fetch_add(1, Ordering::SeqCst) < 2 {
                Err(ArangoError::Conflict("busy".into()))
            } else {
                Ok(42)
            }
        })
        .await;
        assert_eq!(result.unwrap(), 42);
        assert_eq!(calls.load(Ordering::SeqCst), 3);

        let calls = AtomicU32::new(0);
        let result: Result<(), _> = retry_on_conflict(2, || async {
            calls.fetch_add(1, Ordering::SeqCst);
            Err(ArangoError::Conflict("busy".into()))
        })
        .await;
        assert!(result.unwrap_err().is_conflict());
        assert_eq!(calls.load(Ordering::SeqCst), 2);

        let calls = AtomicU32::new(0);
        let result: Result<(), _> = retry_on_conflict(5, || async {
            calls.fetch_add(1, Ordering::SeqCst);
            Err(ArangoError::Decode("broken".into()))
        })
        .await;
        assert!(result.is_err());
        assert_eq!(calls.load(Ordering::SeqCst), 1);
    }
}
