//! Integration tests against real ArangoDB 3.12 and Mailpit containers (ADR-0009).
//!
//! Custom harness (`harness = false`): the two containers start once for the
//! whole run and every scenario works in its own database. Filter scenarios
//! with `cargo test --test integration -- <substring>`.

mod support;

use std::collections::HashSet;
use std::future::Future;
use std::pin::Pin;
use std::process::ExitCode;
use std::sync::Arc;
use std::time::{Duration, Instant};

use launchkit_worker::arango::{ArangoClient, ArangoError, ERROR_UNIQUE_CONSTRAINT};
use launchkit_worker::jobs::{JobQueue, JobStatus};
use launchkit_worker::worker::{PAYMENT_WEBHOOK, SEND_EMAIL};
use launchkit_worker::{Worker, clock};
use serde_json::{Value, json};
use support::{APP_BASE_URL, Env, all, get, insert, job, make_due};

type Scenario = fn(Arc<Env>) -> Pin<Box<dyn Future<Output = ()> + Send>>;

macro_rules! scenarios {
    ($($name:ident),* $(,)?) => {
        vec![$((stringify!($name), (|env| Box::pin($name(env))) as Scenario)),*]
    };
}

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let all_scenarios = scenarios![
        arango_client_follows_cursors_and_maps_errors,
        write_conflict_is_reported_as_retryable,
        verify_email_is_delivered_and_link_redacted,
        invitation_email_is_rendered_in_english,
        payment_webhook_upgrades_once_and_is_idempotent,
        payment_webhook_reports_missing_data,
        smtp_unreachable_retries_with_backoff_then_fails,
        unknown_job_type_fails_after_max_attempts,
        invitation_expiry_decrements_pending_counter,
        concurrent_claims_never_return_the_same_job,
        stale_running_jobs_are_released,
        run_loop_processes_jobs_and_stops_on_shutdown,
        shutdown_waits_for_the_current_job,
        binary_exits_cleanly_on_sigterm,
    ];
    if args.iter().any(|a| a == "--list") {
        for (name, _) in &all_scenarios {
            println!("{name}: test");
        }
        return ExitCode::SUCCESS;
    }
    let filters: Vec<&str> = args
        .iter()
        .filter(|a| !a.starts_with('-'))
        .map(String::as_str)
        .collect();
    let selected: Vec<(&str, Scenario)> = all_scenarios
        .into_iter()
        .filter(|(name, _)| filters.is_empty() || filters.iter().any(|f| name.contains(f)))
        .collect();
    if selected.is_empty() {
        println!("\nrunning 0 integration scenarios");
        return ExitCode::SUCCESS;
    }
    tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .expect("tokio runtime")
        .block_on(run(selected))
}

async fn run(selected: Vec<(&'static str, Scenario)>) -> ExitCode {
    println!("\nrunning {} integration scenarios", selected.len());
    let started = Instant::now();
    let containers = match support::Containers::start().await {
        Ok(c) => c,
        Err(e) => {
            eprintln!("cannot start test containers: {e:#}");
            return ExitCode::FAILURE;
        }
    };
    println!(
        "containers ready in {:.1}s",
        started.elapsed().as_secs_f64()
    );
    let env = Arc::new(containers.env.clone());
    let mut failed = Vec::new();
    for (name, scenario) in &selected {
        let t = Instant::now();
        // A panic stays inside the task, so containers are always removed.
        let outcome = tokio::spawn(scenario(env.clone())).await;
        let secs = t.elapsed().as_secs_f64();
        if outcome.is_ok() {
            println!("test {name} ... ok ({secs:.1}s)");
        } else {
            println!("test {name} ... FAILED ({secs:.1}s)");
            failed.push(*name);
        }
    }
    containers.stop().await;
    let passed = selected.len() - failed.len();
    if failed.is_empty() {
        println!(
            "\ntest result: ok. {passed} passed; 0 failed; finished in {:.1}s",
            started.elapsed().as_secs_f64()
        );
        ExitCode::SUCCESS
    } else {
        println!("\nfailures: {failed:?}");
        println!(
            "test result: FAILED. {passed} passed; {} failed",
            failed.len()
        );
        ExitCode::FAILURE
    }
}

// ---------------------------------------------------------------- seeds

struct Org {
    owner_key: String,
    org_key: String,
}

async fn seed_org(db: &ArangoClient, email: &str, name: &str) -> Org {
    let now = clock::iso(clock::now());
    let owner_key = insert(
        db,
        "users",
        json!({"email": email, "name": name, "passwordHash": "x", "emailVerified": true,
               "emailVerifiedAt": now, "isSuperAdmin": false, "locale": "fr", "createdAt": now}),
    )
    .await;
    let org_key = insert(
        db,
        "organizations",
        json!({"name": "Acme", "plan": "free", "ownerId": owner_key,
               "usage": {"projects": 1, "members": 1, "pendingInvitations": 0},
               "createdAt": now, "upgradedAt": null}),
    )
    .await;
    insert(
        db,
        "memberships",
        json!({"_from": format!("users/{owner_key}"), "_to": format!("organizations/{org_key}"),
               "role": "owner", "joinedAt": now}),
    )
    .await;
    Org { owner_key, org_key }
}

async fn seed_payment(db: &ArangoClient, org: &Org) -> String {
    let now = clock::iso(clock::now());
    insert(
        db,
        "payments",
        json!({"orgId": org.org_key, "provider": "simulated", "plan": "pro", "amount": 2900,
               "currency": "EUR", "status": "pending", "createdBy": org.owner_key,
               "createdAt": now, "completedAt": null}),
    )
    .await
}

fn verify_job(to: &str, link: &str) -> Value {
    job(
        SEND_EMAIL,
        json!({"template": "verify_email", "to": to, "locale": "fr",
               "params": {"firstName": "Marie", "link": link, "hours": 24}}),
    )
}

// ---------------------------------------------------------------- scenarios

async fn arango_client_follows_cursors_and_maps_errors(env: Arc<Env>) {
    let db = env.database("t_client").await;
    assert!(
        !db.create_database().await.unwrap(),
        "second create is a no-op"
    );
    assert!(
        env.client("_system")
            .version()
            .await
            .unwrap()
            .starts_with("3.12")
    );

    // 2 500 rows with batches of 1 000: three cursor pages.
    let rows: Vec<u32> = db
        .query("FOR i IN 1..@n RETURN i", json!({"n": 2500}))
        .await
        .unwrap();
    assert_eq!(rows.len(), 2500);
    assert_eq!(rows[2499], 2500);

    // Bind variables are data, never AQL.
    let injected: Vec<Value> = db
        .query(
            "FOR u IN users FILTER u.email == @email RETURN u",
            json!({"email": "\" || true || \""}),
        )
        .await
        .unwrap();
    assert!(injected.is_empty());

    let syntax = db.query::<Value>("FOR x IN", json!({})).await.unwrap_err();
    assert!(
        matches!(syntax, ArangoError::Server { num: 1501, .. }),
        "{syntax:?}"
    );

    let wrong_password = ArangoClient::new(&env.arango_url, "t_client", "root", "wrong").unwrap();
    let denied = wrong_password
        .query::<Value>("RETURN 1", json!({}))
        .await
        .unwrap_err();
    assert!(
        matches!(denied, ArangoError::Server { status: 401, .. }),
        "{denied:?}"
    );

    let unreachable = ArangoClient::new("http://127.0.0.1:1", "x", "root", "").unwrap();
    assert!(matches!(
        unreachable.version().await.unwrap_err(),
        ArangoError::Transport(_)
    ));

    insert(&db, "users", json!({"email": "dup@demo.test"})).await;
    let err = db
        .query::<Value>(
            "INSERT {email: @e} INTO users",
            json!({"e": "dup@demo.test"}),
        )
        .await
        .unwrap_err();
    assert_eq!(err.error_num(), Some(ERROR_UNIQUE_CONSTRAINT));
}

async fn write_conflict_is_reported_as_retryable(env: Arc<Env>) {
    let db = env.database("t_conflict").await;
    let key = insert(&db, "jobs", job("noop", json!({}))).await;
    let base = format!("{}/_db/t_conflict/_api", env.arango_url);
    let http = support::http();

    // A stream transaction updates the job and keeps its lock.
    let begin: Value = http
        .post(format!("{base}/transaction/begin"))
        .basic_auth("root", Some(support::ARANGO_PASSWORD))
        .json(&json!({"collections": {"write": ["jobs"]}}))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    let trx = begin["result"]["id"].as_str().unwrap().to_owned();
    let patched = http
        .patch(format!("{base}/document/jobs/{key}"))
        .basic_auth("root", Some(support::ARANGO_PASSWORD))
        .header("x-arango-trx-id", &trx)
        .json(&json!({"lockedAt": "held"}))
        .send()
        .await
        .unwrap();
    assert!(patched.status().is_success());

    let err = db
        .query::<Value>(
            "UPDATE @key WITH { status: 'running' } IN jobs",
            json!({"key": key}),
        )
        .await
        .unwrap_err();
    assert!(err.is_conflict(), "expected error 1200, got {err:?}");

    http.delete(format!("{base}/transaction/{trx}"))
        .basic_auth("root", Some(support::ARANGO_PASSWORD))
        .send()
        .await
        .unwrap();
}

async fn verify_email_is_delivered_and_link_redacted(env: Arc<Env>) {
    let db = env.database("t_verify").await;
    let worker = env.worker(&db);
    let to = "marie.verify@demo.test";
    let link = format!("{APP_BASE_URL}/verify-email?token=s3cr3t-token&lang=fr");
    let key = insert(&db, "jobs", verify_job(to, &link)).await;

    let processed = worker.run_once().await.unwrap().expect("a job");
    assert_eq!(processed.key, key);
    assert_eq!(processed.status, JobStatus::Done);
    assert!(worker.run_once().await.unwrap().is_none(), "queue is empty");

    let stored = get(&db, "jobs", &key).await;
    assert_eq!(stored["status"], "done");
    assert_eq!(stored["attempts"], 1);
    assert_eq!(stored["lastError"], Value::Null);
    assert!(clock::parse(stored["finishedAt"].as_str().unwrap()).is_some());
    assert_eq!(stored["payload"]["params"]["link"], "[envoyé]");
    assert_eq!(stored["payload"]["params"]["firstName"], "Marie");
    assert!(
        !stored.to_string().contains("s3cr3t-token"),
        "token left in the database"
    );

    let message = env.single_message_to(to).await;
    assert_eq!(message["Subject"], "Confirmez votre adresse e-mail");
    assert_eq!(message["From"]["Address"], "noreply@demo.test");
    let recipients: Vec<&str> = message["To"]
        .as_array()
        .unwrap()
        .iter()
        .map(|t| t["Address"].as_str().unwrap())
        .collect();
    assert_eq!(recipients, vec![to]);
    assert!(message["Cc"].as_array().is_none_or(Vec::is_empty));
    assert!(message["Bcc"].as_array().is_none_or(Vec::is_empty));
    let html = message["HTML"].as_str().unwrap();
    assert!(
        html.contains("href=\"http://localhost:5173/verify-email?token=s3cr3t-token&amp;lang=fr\"")
    );
    assert!(html.contains("Confirmez votre adresse"));
    assert!(html.contains("cid:logo") && html.contains("cid:illustration"));
    let text = message["Text"].as_str().unwrap();
    assert!(text.contains("Bonjour Marie"));
    assert!(text.contains(&link));
    let inline: HashSet<&str> = message["Inline"]
        .as_array()
        .unwrap()
        .iter()
        .map(|p| p["ContentID"].as_str().unwrap())
        .collect();
    assert_eq!(inline, HashSet::from(["logo", "illustration"]));
}

async fn invitation_email_is_rendered_in_english(env: Arc<Env>) {
    let db = env.database("t_invite").await;
    let worker = env.worker(&db);
    let to = "paul.invite@demo.test";
    insert(
        &db,
        "jobs",
        job(
            SEND_EMAIL,
            json!({"template": "invitation", "to": to, "locale": "en",
                   "params": {"inviter": "Marie", "org": "Acme", "role": "admin",
                              "link": format!("{APP_BASE_URL}/invitations/tok"), "days": 7}}),
        ),
    )
    .await;
    assert_eq!(
        worker.run_once().await.unwrap().unwrap().status,
        JobStatus::Done
    );
    let message = env.single_message_to(to).await;
    assert_eq!(message["Subject"], "Marie invited you to join Acme");
    let text = message["Text"].as_str().unwrap();
    assert!(text.contains("as Admin."), "{text}");
    assert!(text.contains("The invitation is valid for 7 days."));
}

async fn payment_webhook_upgrades_once_and_is_idempotent(env: Arc<Env>) {
    let db = env.database("t_payment").await;
    let worker = env.worker(&db);
    let owner_email = "marie.pro@demo.test";
    let org = seed_org(&db, owner_email, "  Marie Curie").await;
    let payment = seed_payment(&db, &org).await;
    let first = insert(
        &db,
        "jobs",
        job(PAYMENT_WEBHOOK, json!({"paymentId": payment})),
    )
    .await;

    let processed = worker.run_once().await.unwrap().unwrap();
    assert_eq!(processed.key, first);
    assert_eq!(processed.status, JobStatus::Done);

    let p = get(&db, "payments", &payment).await;
    assert_eq!(p["status"], "succeeded");
    assert!(clock::parse(p["completedAt"].as_str().unwrap()).is_some());
    let o = get(&db, "organizations", &org.org_key).await;
    assert_eq!(o["plan"], "pro");
    assert!(clock::parse(o["upgradedAt"].as_str().unwrap()).is_some());
    assert_eq!(o["usage"]["projects"], 1, "other fields untouched");

    let activities = all(&db, "activities").await;
    assert_eq!(activities.len(), 1);
    let a = &activities[0];
    assert_eq!(a["orgId"], org.org_key.as_str());
    assert_eq!(a["projectId"], Value::Null);
    assert_eq!(a["actorId"], Value::Null);
    assert_eq!(a["type"], "plan_upgraded");
    assert_eq!(a["data"], json!({"plan": "pro"}));
    assert!(a["createdAt"].is_string());

    let welcome_jobs = |jobs: Vec<Value>| -> Vec<Value> {
        jobs.into_iter()
            .filter(|j| j["payload"]["template"] == "pro_welcome")
            .collect()
    };
    let welcome = welcome_jobs(all(&db, "jobs").await);
    assert_eq!(welcome.len(), 1);
    let w = &welcome[0];
    assert_eq!(w["type"], "send_email");
    assert_eq!(w["status"], "pending");
    assert_eq!(w["attempts"], 0);
    assert_eq!(w["maxAttempts"], 5);
    assert_eq!(w["runAt"], w["createdAt"]);
    assert_eq!(
        w["payload"],
        json!({"template": "pro_welcome", "to": owner_email, "locale": "fr",
               "params": {"firstName": "Marie", "org": "Acme",
                          "link": format!("{APP_BASE_URL}/app/{}", org.org_key)}})
    );

    // Replay: same payment, new webhook job, nothing more happens.
    let replay = insert(
        &db,
        "jobs",
        job(PAYMENT_WEBHOOK, json!({"paymentId": payment})),
    )
    .await;
    let mut processed = Vec::new();
    while let Some(p) = worker.run_once().await.unwrap() {
        processed.push(p);
    }
    // The pro_welcome e-mail and the replay.
    assert_eq!(processed.len(), 2);
    assert!(processed.iter().all(|p| p.status == JobStatus::Done));
    assert!(processed.iter().any(|p| p.key == replay));
    assert_eq!(all(&db, "activities").await.len(), 1);
    assert_eq!(welcome_jobs(all(&db, "jobs").await).len(), 1);
    assert_eq!(get(&db, "jobs", &replay).await["status"], "done");

    let message = env.single_message_to(owner_email).await;
    assert_eq!(message["Subject"], "Acme passe en Pro");
    let welcome_after = welcome_jobs(all(&db, "jobs").await);
    assert_eq!(welcome_after[0]["payload"]["params"]["link"], "[envoyé]");
}

async fn payment_webhook_reports_missing_data(env: Arc<Env>) {
    let db = env.database("t_payment_missing").await;
    let worker = env.worker(&db);
    let now = clock::iso(clock::now());

    let no_org = insert(
        &db,
        "payments",
        json!({"orgId": "nope", "status": "pending", "createdAt": now}),
    )
    .await;
    let org_key = insert(
        &db,
        "organizations",
        json!({"name": "Ghost", "plan": "free", "ownerId": "nobody", "usage": {}}),
    )
    .await;
    let no_owner = insert(
        &db,
        "payments",
        json!({"orgId": org_key, "status": "pending", "createdAt": now}),
    )
    .await;

    for (kind, payload, expected) in [
        (
            PAYMENT_WEBHOOK,
            json!({"paymentId": "missing"}),
            "payment missing not found",
        ),
        (
            PAYMENT_WEBHOOK,
            json!({"paymentId": no_org}),
            "organization of payment",
        ),
        (
            PAYMENT_WEBHOOK,
            json!({"paymentId": no_owner}),
            "owner of the organization",
        ),
        (
            PAYMENT_WEBHOOK,
            json!({"payment": 1}),
            "invalid payment_webhook payload",
        ),
        (
            SEND_EMAIL,
            json!({"template": "newsletter"}),
            "invalid send_email payload",
        ),
    ] {
        let key = insert(&db, "jobs", job(kind, payload)).await;
        let processed = worker.run_once().await.unwrap().unwrap();
        assert_eq!(processed.key, key);
        assert!(matches!(processed.status, JobStatus::Retry { .. }));
        let stored = get(&db, "jobs", &key).await;
        assert_eq!(stored["status"], "pending");
        assert!(
            stored["lastError"].as_str().unwrap().contains(expected),
            "{} does not contain {expected}",
            stored["lastError"]
        );
    }
    assert!(all(&db, "activities").await.is_empty());
    assert_eq!(get(&db, "payments", &no_owner).await["status"], "pending");
}

async fn smtp_unreachable_retries_with_backoff_then_fails(env: Arc<Env>) {
    let db = env.database("t_retry").await;
    let worker = env.worker_with_smtp_port(&db, support::closed_port());
    let to = "marie.retry@demo.test";
    let link = format!("{APP_BASE_URL}/verify-email?token=retry");
    let key = insert(&db, "jobs", verify_job(to, &link)).await;

    for attempt in 1..=5u32 {
        let before = clock::now();
        let processed = worker.run_once().await.unwrap().expect("job is due");
        let after = clock::now();
        let stored = get(&db, "jobs", &key).await;
        assert_eq!(stored["attempts"], attempt);
        assert!(
            stored["lastError"]
                .as_str()
                .unwrap()
                .contains("SMTP delivery failed"),
            "{}",
            stored["lastError"]
        );
        if attempt < 5 {
            assert_eq!(stored["status"], "pending");
            assert!(matches!(processed.status, JobStatus::Retry { .. }));
            assert_eq!(stored["lockedAt"], Value::Null);
            let run_at = clock::parse(stored["runAt"].as_str().unwrap()).unwrap();
            let delay = chrono::Duration::seconds(1 << attempt);
            let ms = chrono::Duration::milliseconds(1);
            assert!(
                run_at >= before + delay - ms && run_at <= after + delay + ms,
                "attempt {attempt}: runAt {run_at} is not {}s after {before}",
                1 << attempt
            );
            // Not due yet: nothing to claim before the backoff delay.
            assert!(worker.run_once().await.unwrap().is_none());
            make_due(&db, &key).await;
        } else {
            assert_eq!(processed.status, JobStatus::Failed);
            assert_eq!(stored["status"], "failed");
            assert!(clock::parse(stored["finishedAt"].as_str().unwrap()).is_some());
            // The clear token does not stay in the database once the e-mail is given up.
            assert_eq!(stored["payload"]["params"]["link"], "[retiré]");
            assert_eq!(stored["payload"]["to"], to);
        }
    }
    assert!(
        worker.run_once().await.unwrap().is_none(),
        "failed jobs are not retried"
    );
    assert!(env.messages_to(to).await.is_empty());
}

async fn unknown_job_type_fails_after_max_attempts(env: Arc<Env>) {
    let db = env.database("t_unknown").await;
    let worker = env.worker(&db);
    let mut doc = job("make_coffee", json!({}));
    doc["maxAttempts"] = json!(1);
    let key = insert(&db, "jobs", doc).await;
    let processed = worker.run_once().await.unwrap().unwrap();
    assert_eq!(processed.status, JobStatus::Failed);
    let stored = get(&db, "jobs", &key).await;
    assert_eq!(stored["status"], "failed");
    assert_eq!(stored["lastError"], "unknown job type \"make_coffee\"");
}

async fn invitation_expiry_decrements_pending_counter(env: Arc<Env>) {
    let db = env.database("t_expiry").await;
    let worker = env.worker(&db);
    let now = clock::now();
    let past = clock::iso(clock::sub(now, Duration::from_secs(3600)));
    let future = clock::iso(clock::add(now, Duration::from_secs(3600)));
    let org = |name: &str, pending: u32| {
        json!({"name": name, "plan": "free", "ownerId": "u",
               "usage": {"projects": 2, "members": 1, "pendingInvitations": pending}})
    };
    let acme = insert(&db, "organizations", org("Acme", 3)).await;
    let beta = insert(&db, "organizations", org("Beta", 1)).await;
    let gamma = insert(&db, "organizations", org("Gamma", 1)).await;
    let rows = [
        (&acme, "pending", &past),
        (&acme, "pending", &past),
        (&acme, "pending", &future),
        (&acme, "accepted", &past),
        (&beta, "pending", &past),
        (&gamma, "pending", &future),
    ];
    let mut keys = Vec::new();
    for (n, (org_id, status, expires_at)) in rows.into_iter().enumerate() {
        let doc = json!({"orgId": org_id, "email": format!("guest{n}@demo.test"),
                         "role": "member", "tokenHash": format!("hash-{n}"), "invitedBy": "u",
                         "status": status, "createdAt": past, "expiresAt": expires_at,
                         "respondedAt": null});
        keys.push(insert(&db, "invitations", doc).await);
    }

    worker.run_maintenance().await;

    let mut statuses = Vec::new();
    for k in &keys {
        statuses.push(get(&db, "invitations", k).await["status"].clone());
    }
    assert_eq!(
        statuses,
        json!([
            "expired", "expired", "pending", "accepted", "expired", "pending"
        ])
        .as_array()
        .unwrap()
        .clone()
    );
    let pending = |doc: Value| doc["usage"]["pendingInvitations"].clone();
    let acme_doc = get(&db, "organizations", &acme).await;
    assert_eq!(
        acme_doc["usage"],
        json!({"projects": 2, "members": 1, "pendingInvitations": 1})
    );
    assert_eq!(pending(get(&db, "organizations", &beta).await), 0);
    assert_eq!(pending(get(&db, "organizations", &gamma).await), 1);

    // Running it again changes nothing.
    assert_eq!(launchkit_worker::invitations::expire(&db).await.unwrap(), 0);
    assert_eq!(pending(get(&db, "organizations", &acme).await), 1);
}

async fn concurrent_claims_never_return_the_same_job(env: Arc<Env>) {
    let db = env.database("t_concurrency").await;
    const JOBS: usize = 40;
    const WORKERS: usize = 8;
    for _ in 0..JOBS {
        insert(&db, "jobs", job("noop", json!({}))).await;
    }
    let mut tasks = Vec::new();
    for _ in 0..WORKERS {
        let queue = JobQueue::new(db.clone());
        tasks.push(tokio::spawn(async move {
            let mut claimed = Vec::new();
            loop {
                match queue.claim().await {
                    Ok(Some(job)) => claimed.push(job.key),
                    Ok(None) => break,
                    Err(e) if e.is_conflict() => continue,
                    Err(e) => panic!("claim failed: {e}"),
                }
            }
            claimed
        }));
    }
    let mut keys = Vec::new();
    for t in tasks {
        keys.extend(t.await.unwrap());
    }
    let unique: HashSet<&String> = keys.iter().collect();
    assert_eq!(unique.len(), keys.len(), "a job was claimed twice");
    assert_eq!(keys.len(), JOBS);
    for j in all(&db, "jobs").await {
        assert_eq!(j["status"], "running");
        assert_eq!(j["attempts"], 1);
    }
}

async fn stale_running_jobs_are_released(env: Arc<Env>) {
    let db = env.database("t_stale").await;
    let worker = env.worker(&db);
    let now = clock::now();
    let old = clock::iso(clock::sub(now, Duration::from_secs(600)));
    let running = |attempts: u32, locked_at: &str| {
        let mut j = job("noop", json!({}));
        j["status"] = json!("running");
        j["attempts"] = json!(attempts);
        j["lockedAt"] = json!(locked_at);
        j
    };
    let stale = insert(&db, "jobs", running(1, &old)).await;
    let exhausted = insert(&db, "jobs", running(5, &old)).await;
    let fresh = insert(&db, "jobs", running(1, &clock::iso(now))).await;
    let mut mail = job(
        "send_email",
        json!({"template": "verify_email", "to": "marie@demo.test", "locale": "fr",
               "params": {"firstName": "Marie", "link": "http://web.test/verify-email?token=secret", "hours": 24}}),
    );
    mail["status"] = json!("running");
    mail["attempts"] = json!(5);
    mail["lockedAt"] = json!(old);
    let exhausted_mail = insert(&db, "jobs", mail).await;

    worker.run_maintenance().await;

    let s = get(&db, "jobs", &stale).await;
    assert_eq!(s["status"], "pending");
    assert_eq!(s["lockedAt"], Value::Null);
    assert!(s["lastError"].as_str().unwrap().contains("worker stopped"));
    let e = get(&db, "jobs", &exhausted).await;
    assert_eq!(e["status"], "failed");
    assert!(e["finishedAt"].is_string());
    assert_eq!(get(&db, "jobs", &fresh).await["status"], "running");
    // Given up: the link (clear token) is removed, the rest of the payload is kept.
    let m = get(&db, "jobs", &exhausted_mail).await;
    assert_eq!(m["status"], "failed");
    assert_eq!(m["payload"]["params"]["link"], "[retiré]");
    assert_eq!(m["payload"]["params"]["firstName"], "Marie");
    assert_eq!(
        e["payload"],
        json!({}),
        "payloads without a link are untouched"
    );

    // A late result from the stopped run is ignored: its lock is gone.
    let mut late = worker.queue().claim().await.unwrap().unwrap();
    assert_eq!(late.key, stale);
    late.locked_at = Some(old.clone());
    assert!(!worker.queue().complete(&late, &json!({})).await.unwrap());
    assert_eq!(get(&db, "jobs", &stale).await["status"], "running");
}

async fn run_loop_processes_jobs_and_stops_on_shutdown(env: Arc<Env>) {
    let db = env.database("t_loop").await;
    let worker: Worker = env.worker(&db);
    let (stop, stopped) = tokio::sync::watch::channel(false);
    let handle = tokio::spawn({
        let worker = worker.clone();
        async move { worker.run(stopped).await }
    });

    let to = "marie.loop@demo.test";
    let key = insert(
        &db,
        "jobs",
        verify_job(to, &format!("{APP_BASE_URL}/verify-email?token=loop")),
    )
    .await;
    let past = clock::iso(clock::sub(clock::now(), Duration::from_secs(60)));
    let org = insert(
        &db,
        "organizations",
        json!({"name": "Loop", "usage": {"pendingInvitations": 1}}),
    )
    .await;
    let invitation = insert(
        &db,
        "invitations",
        json!({"orgId": org, "status": "pending", "expiresAt": past, "tokenHash": "loop"}),
    )
    .await;

    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        let job_done = get(&db, "jobs", &key).await["status"] == "done";
        let expired = get(&db, "invitations", &invitation).await["status"] == "expired";
        if job_done && expired {
            break;
        }
        assert!(Instant::now() < deadline, "loop did not process the work");
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    env.single_message_to(to).await;

    stop.send(true).unwrap();
    tokio::time::timeout(Duration::from_secs(5), handle)
        .await
        .expect("worker stops on shutdown")
        .unwrap();

    // Dropping the sender also stops a worker.
    let (stop, stopped) = tokio::sync::watch::channel(false);
    let handle = tokio::spawn(async move { worker.run(stopped).await });
    tokio::time::sleep(Duration::from_millis(200)).await;
    drop(stop);
    tokio::time::timeout(Duration::from_secs(5), handle)
        .await
        .expect("worker stops when the sender is dropped")
        .unwrap();
}

async fn binary_exits_cleanly_on_sigterm(env: Arc<Env>) {
    let db = env.database("t_binary").await;
    let to = "marie.binary@demo.test";
    let key = insert(
        &db,
        "jobs",
        verify_job(to, &format!("{APP_BASE_URL}/verify-email?token=bin")),
    )
    .await;

    let mut child = std::process::Command::new(env!("CARGO_BIN_EXE_launchkit-worker"))
        .env_clear()
        .env("ARANGO_URL", &env.arango_url)
        .env("ARANGO_DATABASE", "t_binary")
        .env("ARANGO_PASSWORD", support::ARANGO_PASSWORD)
        .env("SMTP_HOST", &env.smtp_host)
        .env("SMTP_PORT", env.smtp_port.to_string())
        .env("I18N_DIR", support::i18n_dir())
        .env("POLL_INTERVAL_MS", "50")
        .env("RUST_LOG", "warn")
        .envs(std::env::vars().filter(|(k, _)| k.starts_with("LLVM_PROFILE")))
        .spawn()
        .expect("worker binary starts");

    let deadline = Instant::now() + Duration::from_secs(15);
    while get(&db, "jobs", &key).await["status"] != "done" {
        assert!(Instant::now() < deadline, "binary did not process the job");
        assert!(child.try_wait().unwrap().is_none(), "binary exited early");
        tokio::time::sleep(Duration::from_millis(100)).await;
    }
    env.single_message_to(to).await;

    let killed = std::process::Command::new("kill")
        .args(["-TERM", &child.id().to_string()])
        .status()
        .unwrap();
    assert!(killed.success());
    let deadline = Instant::now() + Duration::from_secs(5);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() > deadline {
            let _ = child.kill();
            panic!("binary still running 5 s after SIGTERM");
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
    };
    assert!(status.success(), "exit status {status}");

    // Invalid configuration: clear error, non-zero exit.
    let output = std::process::Command::new(env!("CARGO_BIN_EXE_launchkit-worker"))
        .env_clear()
        .env("SMTP_PORT", "not-a-port")
        .envs(std::env::vars().filter(|(k, _)| k.starts_with("LLVM_PROFILE")))
        .output()
        .unwrap();
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("SMTP_PORT"));
}

async fn shutdown_waits_for_the_current_job(env: Arc<Env>) {
    let db = env.database("t_graceful").await;
    // An SMTP server that accepts the connection but stays silent for 1 s:
    // the job is still sending when shutdown is requested.
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    std::thread::spawn(move || {
        if let Ok((socket, _)) = listener.accept() {
            std::thread::sleep(Duration::from_secs(1));
            drop(socket);
        }
    });
    let worker = env.worker_with_smtp_port(&db, port);
    let key = insert(
        &db,
        "jobs",
        verify_job(
            "marie.graceful@demo.test",
            &format!("{APP_BASE_URL}/verify-email?token=graceful"),
        ),
    )
    .await;

    let (stop, stopped) = tokio::sync::watch::channel(false);
    let handle = tokio::spawn(async move { worker.run(stopped).await });
    let deadline = Instant::now() + Duration::from_secs(5);
    while get(&db, "jobs", &key).await["status"] != "running" {
        assert!(Instant::now() < deadline, "job not claimed");
        tokio::time::sleep(Duration::from_millis(20)).await;
    }
    stop.send(true).unwrap();
    tokio::time::timeout(Duration::from_secs(15), handle)
        .await
        .expect("worker stops after the job")
        .unwrap();

    // The run was finished and recorded, not abandoned in `running`.
    let stored = get(&db, "jobs", &key).await;
    assert_eq!(stored["status"], "pending");
    assert_eq!(stored["attempts"], 1);
    assert!(stored["lastError"].as_str().unwrap().contains("SMTP"));
}
