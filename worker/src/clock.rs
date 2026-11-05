//! Date helpers. The data contract stores dates as ISO 8601 UTC strings with
//! milliseconds (`2026-11-04T09:30:00.000Z`), which sort correctly as strings.

use std::time::Duration;

use chrono::{DateTime, SecondsFormat, Utc};

/// Current time in UTC.
pub fn now() -> DateTime<Utc> {
    Utc::now()
}

/// Formats a date in the contract format.
pub fn iso(at: DateTime<Utc>) -> String {
    at.to_rfc3339_opts(SecondsFormat::Millis, true)
}

/// `at + delay`, saturating at the maximum representable date.
pub fn add(at: DateTime<Utc>, delay: Duration) -> DateTime<Utc> {
    chrono::Duration::from_std(delay)
        .ok()
        .and_then(|d| at.checked_add_signed(d))
        .unwrap_or(DateTime::<Utc>::MAX_UTC)
}

/// `at - delay`, saturating at the minimum representable date.
pub fn sub(at: DateTime<Utc>, delay: Duration) -> DateTime<Utc> {
    chrono::Duration::from_std(delay)
        .ok()
        .and_then(|d| at.checked_sub_signed(d))
        .unwrap_or(DateTime::<Utc>::MIN_UTC)
}

/// Parses a contract date back into a `DateTime`.
pub fn parse(value: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value)
        .ok()
        .map(|d| d.with_timezone(&Utc))
}

#[cfg(test)]
mod tests {
    use super::*;
    use chrono::TimeZone;

    #[test]
    fn formats_with_milliseconds_and_z() {
        let at = Utc.with_ymd_and_hms(2026, 11, 4, 9, 30, 0).unwrap();
        assert_eq!(iso(at), "2026-11-04T09:30:00.000Z");
    }

    #[test]
    fn add_and_sub_round_trip() {
        let at = Utc.with_ymd_and_hms(2026, 11, 4, 9, 30, 0).unwrap();
        let later = add(at, Duration::from_secs(16));
        assert_eq!(iso(later), "2026-11-04T09:30:16.000Z");
        assert_eq!(sub(later, Duration::from_secs(16)), at);
    }

    #[test]
    fn add_saturates_instead_of_panicking() {
        let at = Utc.with_ymd_and_hms(2026, 11, 4, 9, 30, 0).unwrap();
        assert_eq!(add(at, Duration::MAX), DateTime::<Utc>::MAX_UTC);
        assert_eq!(sub(at, Duration::MAX), DateTime::<Utc>::MIN_UTC);
    }

    #[test]
    fn parses_contract_dates() {
        let at = parse("2026-11-04T09:30:00.000Z").unwrap();
        assert_eq!(iso(at), "2026-11-04T09:30:00.000Z");
        assert!(parse("not a date").is_none());
    }
}
