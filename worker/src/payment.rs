//! `payment_webhook` jobs: the worker plays the payment provider (ADR-0006).

use serde::Deserialize;
use serde_json::{Value, json};

use crate::arango::{ArangoClient, ArangoError, retry_on_conflict};
use crate::clock;
use crate::jobs::DEFAULT_MAX_ATTEMPTS;

const CONFLICT_TRIES: u32 = 5;

/// Whole upgrade in one AQL statement (one transaction). The `pending` filter
/// makes it idempotent: a replay finds nothing and changes nothing, and two
/// concurrent replays collide on the payment (error 1200) instead of both
/// applying.
pub const UPGRADE_QUERY: &str = r#"
FOR p IN payments
  FILTER p._key == @paymentId AND p.status == "pending"
  LET org = DOCUMENT("organizations", p.orgId)
  FILTER org != null
  LET owner = DOCUMENT("users", org.ownerId)
  FILTER owner != null
  UPDATE p WITH { status: "succeeded", completedAt: @now } IN payments
  UPDATE org WITH { plan: "pro", upgradedAt: @now } IN organizations
  INSERT {
    orgId: org._key, projectId: null, actorId: null, type: "plan_upgraded",
    data: { plan: "pro" }, createdAt: @now
  } INTO activities
  INSERT {
    type: "send_email",
    payload: {
      template: "pro_welcome", to: owner.email, locale: owner.locale,
      params: {
        firstName: FIRST(SPLIT(TRIM(owner.name), " ")),
        org: org.name,
        link: CONCAT(@appBaseUrl, "/app/", org._key)
      }
    },
    status: "pending", attempts: 0, maxAttempts: @maxAttempts, runAt: @now,
    lockedAt: null, lastError: null, createdAt: @now, finishedAt: null
  } INTO jobs
  RETURN { orgId: org._key, emailJobId: NEW._key }
"#;

/// Read-only: explains why [`UPGRADE_QUERY`] changed nothing.
pub const STATE_QUERY: &str = r#"
LET p = DOCUMENT("payments", @paymentId)
LET org = p == null ? null : DOCUMENT("organizations", p.orgId)
LET owner = org == null ? null : DOCUMENT("users", org.ownerId)
RETURN { status: p.status, orgFound: org != null, ownerFound: owner != null }
"#;

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PaymentWebhookPayload {
    pub payment_id: String,
}

impl PaymentWebhookPayload {
    pub fn parse(payload: &Value) -> Result<Self, PaymentError> {
        let parsed =
            Self::deserialize(payload).map_err(|e| PaymentError::InvalidPayload(e.to_string()))?;
        if parsed.payment_id.trim().is_empty() {
            return Err(PaymentError::InvalidPayload("paymentId is empty".into()));
        }
        Ok(parsed)
    }
}

#[derive(Debug, thiserror::Error)]
pub enum PaymentError {
    #[error("invalid payment_webhook payload: {0}")]
    InvalidPayload(String),
    #[error("payment {0} not found")]
    PaymentNotFound(String),
    #[error("organization of payment {0} not found")]
    OrganizationNotFound(String),
    #[error("owner of the organization of payment {0} not found")]
    OwnerNotFound(String),
    #[error(transparent)]
    Database(#[from] ArangoError),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WebhookOutcome {
    /// Payment succeeded, organization now Pro, welcome e-mail queued.
    Upgraded {
        org_id: String,
        email_job_id: String,
    },
    /// Payment no longer `pending`: nothing to do.
    AlreadyProcessed { status: String },
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Upgraded {
    org_id: String,
    email_job_id: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct PaymentState {
    status: Option<String>,
    org_found: bool,
    owner_found: bool,
}

pub async fn process(
    db: &ArangoClient,
    payload: &PaymentWebhookPayload,
    app_base_url: &str,
) -> Result<WebhookOutcome, PaymentError> {
    let vars = json!({
        "paymentId": payload.payment_id,
        "now": clock::iso(clock::now()),
        "appBaseUrl": app_base_url,
        "maxAttempts": DEFAULT_MAX_ATTEMPTS,
    });
    let upgraded: Option<Upgraded> = retry_on_conflict(CONFLICT_TRIES, || async {
        db.query_first(UPGRADE_QUERY, vars.clone()).await
    })
    .await?;
    if let Some(u) = upgraded {
        return Ok(WebhookOutcome::Upgraded {
            org_id: u.org_id,
            email_job_id: u.email_job_id,
        });
    }

    let state: Option<PaymentState> = db
        .query_first(STATE_QUERY, json!({ "paymentId": payload.payment_id }))
        .await?;
    let id = payload.payment_id.clone();
    match state {
        Some(PaymentState {
            status: Some(status),
            ..
        }) if status != "pending" => Ok(WebhookOutcome::AlreadyProcessed { status }),
        Some(PaymentState {
            status: Some(_),
            org_found: false,
            ..
        }) => Err(PaymentError::OrganizationNotFound(id)),
        Some(PaymentState {
            status: Some(_),
            owner_found: false,
            ..
        }) => Err(PaymentError::OwnerNotFound(id)),
        // Pending, but changed between the two reads: let the retry handle it.
        Some(PaymentState {
            status: Some(_), ..
        }) => Err(PaymentError::Database(ArangoError::Conflict(format!(
            "payment {id} changed during processing"
        )))),
        _ => Err(PaymentError::PaymentNotFound(id)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_payment_id() {
        let p = PaymentWebhookPayload::parse(&json!({"paymentId": "123"})).unwrap();
        assert_eq!(p.payment_id, "123");
    }

    #[test]
    fn rejects_bad_payloads() {
        for bad in [
            json!({}),
            json!({"paymentId": 12}),
            json!({"paymentId": " "}),
            json!(null),
        ] {
            assert!(matches!(
                PaymentWebhookPayload::parse(&bad),
                Err(PaymentError::InvalidPayload(_))
            ));
        }
    }
}
