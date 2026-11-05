//! Configuration, read from environment variables only (see README for defaults).

use std::fmt;
use std::path::PathBuf;
use std::time::Duration;

/// Default values, documented in the README.
pub mod defaults {
    pub const ARANGO_URL: &str = "http://localhost:8529";
    pub const ARANGO_DATABASE: &str = "launchkit";
    pub const ARANGO_USER: &str = "root";
    pub const ARANGO_PASSWORD: &str = "";
    pub const SMTP_HOST: &str = "localhost";
    pub const SMTP_PORT: u16 = 1025;
    pub const MAIL_FROM: &str = "LaunchKit <noreply@demo.test>";
    pub const APP_BASE_URL: &str = "http://localhost:5173";
    pub const I18N_DIR: &str = "/app/i18n";
    pub const POLL_INTERVAL_MS: u64 = 1000;
    pub const EXPIRE_INVITATIONS_EVERY_SECS: u64 = 60;
    pub const STALE_JOB_TIMEOUT_SECS: u64 = 300;
}

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum ConfigError {
    #[error("{name} must be a positive integer, got {value:?}")]
    InvalidNumber { name: &'static str, value: String },
    #[error("{name} must be an http(s) URL, got {value:?}")]
    InvalidUrl { name: &'static str, value: String },
}

#[derive(Clone)]
pub struct Config {
    pub arango_url: String,
    pub arango_database: String,
    pub arango_user: String,
    pub arango_password: String,
    pub smtp_host: String,
    pub smtp_port: u16,
    pub mail_from: String,
    /// Without trailing slash.
    pub app_base_url: String,
    pub i18n_dir: PathBuf,
    pub poll_interval: Duration,
    pub expire_invitations_every: Duration,
    pub stale_job_timeout: Duration,
}

impl Config {
    pub fn from_env() -> Result<Self, ConfigError> {
        Self::from_lookup(|name| std::env::var(name).ok())
    }

    /// Builds the configuration from any variable source (used by tests).
    /// Empty values count as unset, except `ARANGO_PASSWORD`.
    pub fn from_lookup(get: impl Fn(&str) -> Option<String>) -> Result<Self, ConfigError> {
        let text = |name: &str, default: &str| {
            get(name)
                .filter(|v| !v.trim().is_empty())
                .map(|v| v.trim().to_owned())
                .unwrap_or_else(|| default.to_owned())
        };
        let number = |name: &'static str, default: u64| -> Result<u64, ConfigError> {
            match get(name).filter(|v| !v.trim().is_empty()) {
                None => Ok(default),
                Some(value) => value
                    .trim()
                    .parse::<u64>()
                    .ok()
                    .filter(|n| *n > 0)
                    .ok_or(ConfigError::InvalidNumber { name, value }),
            }
        };
        let url = |name: &'static str, default: &str| -> Result<String, ConfigError> {
            let value = text(name, default);
            if value.starts_with("http://") || value.starts_with("https://") {
                Ok(value.trim_end_matches('/').to_owned())
            } else {
                Err(ConfigError::InvalidUrl { name, value })
            }
        };

        let smtp_port = number("SMTP_PORT", u64::from(defaults::SMTP_PORT))?;
        let smtp_port = u16::try_from(smtp_port).map_err(|_| ConfigError::InvalidNumber {
            name: "SMTP_PORT",
            value: smtp_port.to_string(),
        })?;

        Ok(Self {
            arango_url: url("ARANGO_URL", defaults::ARANGO_URL)?,
            arango_database: text("ARANGO_DATABASE", defaults::ARANGO_DATABASE),
            arango_user: text("ARANGO_USER", defaults::ARANGO_USER),
            arango_password: get("ARANGO_PASSWORD")
                .unwrap_or_else(|| defaults::ARANGO_PASSWORD.to_owned()),
            smtp_host: text("SMTP_HOST", defaults::SMTP_HOST),
            smtp_port,
            mail_from: text("MAIL_FROM", defaults::MAIL_FROM),
            app_base_url: url("APP_BASE_URL", defaults::APP_BASE_URL)?,
            i18n_dir: PathBuf::from(text("I18N_DIR", defaults::I18N_DIR)),
            poll_interval: Duration::from_millis(number(
                "POLL_INTERVAL_MS",
                defaults::POLL_INTERVAL_MS,
            )?),
            expire_invitations_every: Duration::from_secs(number(
                "EXPIRE_INVITATIONS_EVERY_SECS",
                defaults::EXPIRE_INVITATIONS_EVERY_SECS,
            )?),
            stale_job_timeout: Duration::from_secs(number(
                "STALE_JOB_TIMEOUT_SECS",
                defaults::STALE_JOB_TIMEOUT_SECS,
            )?),
        })
    }
}

// Manual impl so the password never reaches the logs.
impl fmt::Debug for Config {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Config")
            .field("arango_url", &self.arango_url)
            .field("arango_database", &self.arango_database)
            .field("arango_user", &self.arango_user)
            .field("arango_password", &"<redacted>")
            .field("smtp_host", &self.smtp_host)
            .field("smtp_port", &self.smtp_port)
            .field("mail_from", &self.mail_from)
            .field("app_base_url", &self.app_base_url)
            .field("i18n_dir", &self.i18n_dir)
            .field("poll_interval", &self.poll_interval)
            .field("expire_invitations_every", &self.expire_invitations_every)
            .field("stale_job_timeout", &self.stale_job_timeout)
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn config(vars: &[(&str, &str)]) -> Result<Config, ConfigError> {
        let map: HashMap<String, String> = vars
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect();
        Config::from_lookup(|name| map.get(name).cloned())
    }

    #[test]
    fn uses_documented_defaults() {
        let c = config(&[]).unwrap();
        assert_eq!(c.arango_url, "http://localhost:8529");
        assert_eq!(c.arango_database, "launchkit");
        assert_eq!(c.arango_user, "root");
        assert_eq!(c.arango_password, "");
        assert_eq!(c.smtp_host, "localhost");
        assert_eq!(c.smtp_port, 1025);
        assert_eq!(c.mail_from, "LaunchKit <noreply@demo.test>");
        assert_eq!(c.app_base_url, "http://localhost:5173");
        assert_eq!(c.i18n_dir, PathBuf::from("/app/i18n"));
        assert_eq!(c.poll_interval, Duration::from_millis(1000));
        assert_eq!(c.expire_invitations_every, Duration::from_secs(60));
        assert_eq!(c.stale_job_timeout, Duration::from_secs(300));
    }

    #[test]
    fn reads_overrides_and_trims_base_url() {
        let c = config(&[
            ("ARANGO_URL", "http://arangodb:8529/"),
            ("ARANGO_DATABASE", "demo"),
            ("ARANGO_PASSWORD", "secret"),
            ("SMTP_HOST", "mailpit"),
            ("SMTP_PORT", "2525"),
            ("APP_BASE_URL", "https://demo.example/"),
            ("I18N_DIR", "/tmp/i18n"),
            ("POLL_INTERVAL_MS", "250"),
            ("EXPIRE_INVITATIONS_EVERY_SECS", "5"),
            ("STALE_JOB_TIMEOUT_SECS", "30"),
        ])
        .unwrap();
        assert_eq!(c.arango_url, "http://arangodb:8529");
        assert_eq!(c.arango_database, "demo");
        assert_eq!(c.arango_password, "secret");
        assert_eq!(c.smtp_host, "mailpit");
        assert_eq!(c.smtp_port, 2525);
        assert_eq!(c.app_base_url, "https://demo.example");
        assert_eq!(c.i18n_dir, PathBuf::from("/tmp/i18n"));
        assert_eq!(c.poll_interval, Duration::from_millis(250));
        assert_eq!(c.expire_invitations_every, Duration::from_secs(5));
        assert_eq!(c.stale_job_timeout, Duration::from_secs(30));
    }

    #[test]
    fn empty_values_fall_back_to_defaults() {
        let c = config(&[("SMTP_HOST", "  "), ("POLL_INTERVAL_MS", "")]).unwrap();
        assert_eq!(c.smtp_host, "localhost");
        assert_eq!(c.poll_interval, Duration::from_millis(1000));
    }

    #[test]
    fn rejects_invalid_numbers() {
        assert_eq!(
            config(&[("POLL_INTERVAL_MS", "fast")]).unwrap_err(),
            ConfigError::InvalidNumber {
                name: "POLL_INTERVAL_MS",
                value: "fast".into()
            }
        );
        assert!(config(&[("EXPIRE_INVITATIONS_EVERY_SECS", "0")]).is_err());
        assert!(config(&[("SMTP_PORT", "70000")]).is_err());
    }

    #[test]
    fn rejects_non_http_urls() {
        let err = config(&[("APP_BASE_URL", "javascript:alert(1)")]).unwrap_err();
        assert!(matches!(
            err,
            ConfigError::InvalidUrl {
                name: "APP_BASE_URL",
                ..
            }
        ));
        assert!(err.to_string().contains("APP_BASE_URL"));
    }

    #[test]
    fn debug_output_hides_the_password() {
        let c = config(&[("ARANGO_PASSWORD", "hunter2")]).unwrap();
        let shown = format!("{c:?}");
        assert!(!shown.contains("hunter2"));
        assert!(shown.contains("<redacted>"));
    }
}
