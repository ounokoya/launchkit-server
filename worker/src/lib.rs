//! LaunchKit background worker: runs the jobs the API queues in ArangoDB
//! (e-mails, simulated payment webhook) and expires invitations.
//! Contract: the specification `server-taches.md` (private design repository).

pub mod arango;
pub mod clock;
pub mod config;
pub mod email;
pub mod i18n;
pub mod invitations;
pub mod jobs;
pub mod payment;
pub mod worker;

pub use config::Config;
pub use worker::Worker;
