//! Scheduled task: expire pending invitations past `expiresAt`.

use serde_json::json;

use crate::arango::{ArangoClient, ArangoError, retry_on_conflict};
use crate::clock;

const CONFLICT_TRIES: u32 = 5;

/// One statement: invitations → `expired`, and each organization's
/// `usage.pendingInvitations` decreased by its number of expired invitations.
pub const EXPIRE_QUERY: &str = r#"
LET expired = (
  FOR inv IN invitations
    FILTER inv.status == "pending" AND inv.expiresAt <= @now
    UPDATE inv WITH { status: "expired" } IN invitations
    RETURN NEW.orgId
)
LET updated = (
  FOR orgId IN expired
    COLLECT key = orgId WITH COUNT INTO n
    FOR org IN organizations
      FILTER org._key == key
      UPDATE org WITH {
        usage: { pendingInvitations: MAX([0, org.usage.pendingInvitations - n]) }
      } IN organizations
      RETURN 1
)
RETURN LENGTH(expired)
"#;

/// Returns how many invitations expired.
pub async fn expire(db: &ArangoClient) -> Result<u64, ArangoError> {
    let vars = json!({ "now": clock::iso(clock::now()) });
    let count: Option<u64> = retry_on_conflict(CONFLICT_TRIES, || async {
        db.query_first(EXPIRE_QUERY, vars.clone()).await
    })
    .await?;
    Ok(count.unwrap_or(0))
}
