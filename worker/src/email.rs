//! `send_email` jobs: payload parsing, rendering (subject, hidden preheader,
//! HTML + plain text, inline images) and SMTP delivery.

use std::collections::HashMap;
use std::time::Duration;

use lettre::message::header::ContentType;
use lettre::message::{Attachment, Mailbox, MultiPart, SinglePart};
use lettre::{AsyncSmtpTransport, AsyncTransport, Message, Tokio1Executor};
use serde::Deserialize;
use serde_json::{Map, Value};

use crate::i18n::{Catalog, TextError};

/// Value stored in place of `payload.params.link` once the e-mail is sent:
/// the link carries a clear-text token that must not stay in the database.
pub const SENT_LINK: &str = "[envoyé]";
/// Replaces the link of an e-mail job that will never be sent (no attempt left).
pub const DROPPED_LINK: &str = "[retiré]";

const SMTP_TIMEOUT: Duration = Duration::from_secs(10);
const LOGO_CID: &str = "logo";
const ILLUSTRATION_CID: &str = "illustration";
const BRAND_NAME: &str = "LaunchKit";
const FONT_STACK: &str = "Inter, Arial, sans-serif";

const LOGO_PNG: &[u8] = include_bytes!("../assets/logo.png");
const VERIFY_PNG: &[u8] = include_bytes!("../assets/verifier-email.png");
const INVITATION_PNG: &[u8] = include_bytes!("../assets/invitation.png");
const PRO_PNG: &[u8] = include_bytes!("../assets/pro.png");

/// Roles that can appear in an invitation (never `owner`).
const INVITABLE_ROLES: &[&str] = &["admin", "member"];

#[derive(Debug, thiserror::Error)]
pub enum EmailError {
    #[error("invalid send_email payload: {0}")]
    InvalidPayload(String),
    #[error(transparent)]
    Text(#[from] TextError),
    #[error("invalid address {0:?}")]
    InvalidAddress(String),
    #[error("cannot build message: {0}")]
    Build(String),
    #[error("SMTP delivery failed: {0}")]
    Smtp(#[from] lettre::transport::smtp::Error),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Template {
    VerifyEmail,
    Invitation,
    ProWelcome,
}

impl Template {
    /// Prefix of the template's keys in the i18n files.
    pub fn text_prefix(self) -> &'static str {
        match self {
            Self::VerifyEmail => "email.verify",
            Self::Invitation => "email.invite",
            Self::ProWelcome => "email.pro_welcome",
        }
    }

    /// 240 px illustration shown under the logo.
    pub fn illustration(self) -> &'static [u8] {
        match self {
            Self::VerifyEmail => VERIFY_PNG,
            Self::Invitation => INVITATION_PNG,
            Self::ProWelcome => PRO_PNG,
        }
    }

    /// `pro_welcome` has neither a validity line nor an "ignore" line.
    fn has_expiry_and_ignore(self) -> bool {
        !matches!(self, Self::ProWelcome)
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct SendEmailPayload {
    pub template: Template,
    pub to: String,
    pub locale: String,
    #[serde(default)]
    pub params: Map<String, Value>,
}

impl SendEmailPayload {
    pub fn parse(payload: &Value) -> Result<Self, EmailError> {
        Self::deserialize(payload).map_err(|e| EmailError::InvalidPayload(e.to_string()))
    }
}

/// A fully rendered e-mail, ready to be turned into a MIME message.
#[derive(Debug, Clone)]
pub struct RenderedEmail {
    pub to: Mailbox,
    pub subject: String,
    pub preheader: String,
    pub html: String,
    pub text: String,
    pub illustration: &'static [u8],
}

/// Texts of one e-mail, already formatted (not yet HTML-escaped).
struct Texts {
    subject: String,
    preheader: String,
    title: String,
    body: String,
    button: String,
    expiry: Option<String>,
    ignore: Option<String>,
    footer: String,
}

/// Renders the e-mail. Any missing text or parameter is an error, so a
/// half-empty e-mail is never sent.
pub fn render(catalog: &Catalog, payload: &SendEmailPayload) -> Result<RenderedEmail, EmailError> {
    let to: Mailbox = payload
        .to
        .trim()
        .parse()
        .map_err(|_| EmailError::InvalidAddress(payload.to.clone()))?;
    let locale = payload.locale.as_str();
    let mut params = scalar_params(&payload.params);

    let link = params
        .get("link")
        .cloned()
        .ok_or_else(|| EmailError::InvalidPayload("params.link is required".into()))?;
    if !(link.starts_with("https://") || link.starts_with("http://")) {
        return Err(EmailError::InvalidPayload(
            "params.link must be an http(s) URL".into(),
        ));
    }

    if payload.template == Template::Invitation {
        let role = params
            .get("role")
            .cloned()
            .ok_or_else(|| EmailError::InvalidPayload("params.role is required".into()))?;
        if !INVITABLE_ROLES.contains(&role.as_str()) {
            return Err(EmailError::InvalidPayload(format!(
                "params.role must be admin or member, got {role:?}"
            )));
        }
        let label = catalog.text(locale, &format!("common.role.{role}"))?;
        params.insert("role".into(), label.to_owned());
    }

    let prefix = payload.template.text_prefix();
    let text = |name: &str| catalog.render(locale, &format!("{prefix}.{name}"), &params);
    let optional = |name: &str| {
        payload
            .template
            .has_expiry_and_ignore()
            .then(|| text(name))
            .transpose()
    };
    let texts = Texts {
        subject: single_line(&text("subject")?),
        preheader: text("preheader")?,
        title: text("title")?,
        body: text("body")?,
        button: text("button")?,
        expiry: optional("expiry")?,
        ignore: optional("ignore")?,
        footer: catalog.render(locale, "email.footer", &params)?,
    };

    Ok(RenderedEmail {
        to,
        html: render_html(locale, &texts, &link),
        text: render_text(&texts, &link),
        subject: texts.subject,
        preheader: texts.preheader,
        illustration: payload.template.illustration(),
    })
}

/// Parameters as strings; numbers and booleans are written as is.
fn scalar_params(params: &Map<String, Value>) -> HashMap<String, String> {
    params
        .iter()
        .filter_map(|(k, v)| {
            let value = match v {
                Value::String(s) => s.clone(),
                Value::Number(n) => n.to_string(),
                Value::Bool(b) => b.to_string(),
                _ => return None,
            };
            Some((k.clone(), value))
        })
        .collect()
}

/// Headers cannot contain line breaks.
fn single_line(value: &str) -> String {
    value
        .chars()
        .map(|c| if c.is_control() { ' ' } else { c })
        .collect()
}

/// Escapes text for HTML content and attribute values.
pub fn escape_html(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for c in value.chars() {
        match c {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&#39;"),
            _ => out.push(c),
        }
    }
    out
}

/// HTML version, after the Penpot mock-up « 07 · E-mails »: white 560 px card
/// on #F8FAFC, logo, 240 px illustration, title, text, indigo button,
/// validity line, divider, "ignore" line, footer.
fn render_html(locale: &str, t: &Texts, link: &str) -> String {
    let e = escape_html;
    let font = FONT_STACK;
    // Centred like the Penpot mock-up (page « 07 · E-mails »); only the logo sits on the left.
    let row = |padding: &str, style: &str, content: &str| {
        format!(
            "<tr><td align=\"center\" style=\"padding:{padding};font-family:{font};text-align:center;{style}\">{content}</td></tr>\n"
        )
    };

    let mut rows = String::new();
    rows.push_str(&format!(
        "<tr><td align=\"left\" style=\"padding:32px 40px 0 40px;\"><img src=\"cid:{LOGO_CID}\" width=\"128\" height=\"40\" alt=\"{BRAND_NAME}\" style=\"display:block;border:0;\"></td></tr>\n"
    ));
    rows.push_str(&format!(
        "<tr><td align=\"center\" style=\"padding:24px 40px 0 40px;\"><img src=\"cid:{ILLUSTRATION_CID}\" width=\"240\" height=\"240\" alt=\"\" style=\"display:block;border:0;width:240px;height:240px;\"></td></tr>\n"
    ));
    rows.push_str(&row(
        "24px 40px 0 40px",
        "font-size:24px;line-height:32px;font-weight:700;color:#0F172A;",
        &e(&t.title),
    ));
    rows.push_str(&row(
        "12px 40px 0 40px",
        "font-size:15px;line-height:24px;color:#64748B;",
        &e(&t.body),
    ));
    rows.push_str(&row(
        "24px 40px 0 40px",
        "",
        &format!(
            "<a href=\"{href}\" target=\"_blank\" style=\"display:inline-block;background-color:#4F46E5;color:#FFFFFF;font-family:{font};font-size:15px;line-height:20px;font-weight:600;text-decoration:none;padding:12px 24px;border-radius:8px;\">{label}</a>",
            href = e(link),
            label = e(&t.button),
        ),
    ));
    if let Some(expiry) = &t.expiry {
        rows.push_str(&row(
            "16px 40px 0 40px",
            "font-size:13px;line-height:20px;color:#64748B;",
            &e(expiry),
        ));
    }
    rows.push_str(
        "<tr><td style=\"padding:24px 40px 0 40px;\"><div style=\"border-top:1px solid #E2E8F0;font-size:0;line-height:0;\">&nbsp;</div></td></tr>\n",
    );
    if let Some(ignore) = &t.ignore {
        rows.push_str(&row(
            "8px 40px 0 40px",
            "font-size:13px;line-height:20px;color:#94A3B8;",
            &e(ignore),
        ));
    }
    rows.push_str(&row(
        "16px 40px 32px 40px",
        "font-size:12px;line-height:18px;color:#94A3B8;",
        &e(&t.footer),
    ));

    format!(
        "<!DOCTYPE html>
<html lang=\"{lang}\">
<head>
<meta charset=\"utf-8\">
<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">
<meta name=\"color-scheme\" content=\"light\">
<title>{subject}</title>
</head>
<body style=\"margin:0;padding:0;background-color:#F8FAFC;\">
<div style=\"display:none;max-height:0;max-width:0;overflow:hidden;opacity:0;mso-hide:all;font-size:1px;line-height:1px;color:#F8FAFC;\">{preheader}</div>
<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"background-color:#F8FAFC;\">
<tr><td align=\"center\" style=\"padding:32px 16px;\">
<table role=\"presentation\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:100%;max-width:560px;background-color:#FFFFFF;border-radius:16px;\">
{rows}</table>
</td></tr>
</table>
</body>
</html>
",
        lang = e(locale),
        subject = e(&t.subject),
        preheader = e(&t.preheader),
    )
}

/// Plain-text alternative.
fn render_text(t: &Texts, link: &str) -> String {
    let mut parts = vec![
        t.title.clone(),
        t.body.clone(),
        format!("{}\n{link}", t.button),
    ];
    parts.extend(t.expiry.clone());
    parts.extend(t.ignore.clone());
    parts.push(format!("--\n{}", t.footer));
    parts.join("\n\n") + "\n"
}

/// Builds the MIME message:
/// `multipart/alternative` { `text/plain`, `multipart/related` { `text/html`, logo, illustration } }.
pub fn build_message(from: &Mailbox, email: &RenderedEmail) -> Result<Message, EmailError> {
    let png = ContentType::parse("image/png").map_err(|e| EmailError::Build(e.to_string()))?;
    let related = MultiPart::related()
        .singlepart(SinglePart::html(email.html.clone()))
        .singlepart(Attachment::new_inline(LOGO_CID.into()).body(LOGO_PNG.to_vec(), png.clone()))
        .singlepart(
            Attachment::new_inline(ILLUSTRATION_CID.into()).body(email.illustration.to_vec(), png),
        );
    Message::builder()
        .from(from.clone())
        .to(email.to.clone())
        .subject(email.subject.clone())
        .multipart(
            MultiPart::alternative()
                .singlepart(SinglePart::plain(email.text.clone()))
                .multipart(related),
        )
        .map_err(|e| EmailError::Build(e.to_string()))
}

/// Copy of a `send_email` payload with `params.link` replaced by [`SENT_LINK`].
/// Payloads without a link are returned unchanged.
pub fn redact_link(payload: &Value) -> Value {
    replace_link(payload, SENT_LINK)
}

/// Same as [`redact_link`] with another marker: the clear token never stays in the database.
pub fn replace_link(payload: &Value, marker: &str) -> Value {
    let mut redacted = payload.clone();
    if let Some(link) = redacted
        .get_mut("params")
        .and_then(Value::as_object_mut)
        .and_then(|params| params.get_mut("link"))
    {
        *link = Value::String(marker.into());
    }
    redacted
}

/// SMTP sender (plain SMTP, no TLS nor authentication: Mailpit).
#[derive(Clone)]
pub struct Mailer {
    transport: AsyncSmtpTransport<Tokio1Executor>,
    from: Mailbox,
}

impl Mailer {
    pub fn new(host: &str, port: u16, from: &str) -> Result<Self, EmailError> {
        let from: Mailbox = from
            .parse()
            .map_err(|_| EmailError::InvalidAddress(from.to_owned()))?;
        let transport = AsyncSmtpTransport::<Tokio1Executor>::builder_dangerous(host)
            .port(port)
            .timeout(Some(SMTP_TIMEOUT))
            .build();
        Ok(Self { transport, from })
    }

    pub fn from(&self) -> &Mailbox {
        &self.from
    }

    pub async fn send(&self, email: &RenderedEmail) -> Result<(), EmailError> {
        let message = build_message(&self.from, email)?;
        self.transport.send(message).await?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    use std::path::Path;

    fn shared_catalog() -> Catalog {
        Catalog::load_dir(&Path::new(env!("CARGO_MANIFEST_DIR")).join("../shared/i18n")).unwrap()
    }

    fn payload(value: Value) -> SendEmailPayload {
        SendEmailPayload::parse(&value).unwrap()
    }

    fn verify_fr() -> SendEmailPayload {
        payload(json!({
            "template": "verify_email",
            "to": "marie@demo.test",
            "locale": "fr",
            "params": {"firstName": "Marie", "link": "http://localhost:5173/verify-email?token=abc&x=1", "hours": 24}
        }))
    }

    #[test]
    fn parses_payloads() {
        let p = verify_fr();
        assert_eq!(p.template, Template::VerifyEmail);
        assert_eq!(p.to, "marie@demo.test");
        assert_eq!(p.params["hours"], json!(24));

        let err = SendEmailPayload::parse(
            &json!({"template": "newsletter", "to": "a@b.c", "locale": "fr"}),
        )
        .unwrap_err();
        assert!(matches!(err, EmailError::InvalidPayload(_)));
        assert!(SendEmailPayload::parse(&json!({"template": "verify_email"})).is_err());
        assert!(SendEmailPayload::parse(&json!("not an object")).is_err());
    }

    #[test]
    fn renders_verify_email_in_french() {
        let email = render(&shared_catalog(), &verify_fr()).unwrap();
        assert_eq!(email.subject, "Confirmez votre adresse e-mail");
        assert_eq!(
            email.preheader,
            "Un clic pour activer votre compte LaunchKit."
        );
        assert_eq!(email.to.email.to_string(), "marie@demo.test");
        assert!(email.text.contains("Bonjour Marie, cliquez"));
        assert!(email.text.contains("Ce lien est valable 24 heures."));
        assert!(
            email
                .text
                .contains("http://localhost:5173/verify-email?token=abc&x=1")
        );
        // Link is attribute-escaped in HTML.
        assert!(
            email
                .html
                .contains("href=\"http://localhost:5173/verify-email?token=abc&amp;x=1\"")
        );
        assert!(email.html.contains("cid:logo"));
        assert!(email.html.contains("cid:illustration"));
        assert!(email.html.contains("lang=\"fr\""));
        assert!(email.html.contains("#4F46E5"));
        assert!(email.html.contains("max-width:560px"));
        assert!(email.html.contains("display:none"));
        assert!(
            email
                .html
                .contains("Un clic pour activer votre compte LaunchKit.")
        );
        // NBSP from the source text is kept, not turned into a plain space.
        assert!(email.html.contains("compte\u{a0}? Ignorez"));
        assert!(email.text.contains("compte\u{a0}? Ignorez"));
        assert_eq!(email.illustration, VERIFY_PNG);
    }

    #[test]
    fn renders_invitation_with_translated_role() {
        let p = payload(json!({
            "template": "invitation",
            "to": "paul@demo.test",
            "locale": "en",
            "params": {"inviter": "Marie", "org": "Acme <Labs>", "role": "member", "link": "https://app.test/invitations/t", "days": 7}
        }));
        let email = render(&shared_catalog(), &p).unwrap();
        assert_eq!(email.subject, "Marie invited you to join Acme <Labs>");
        assert!(email.text.contains("as Member."));
        assert!(email.text.contains("The invitation is valid for 7 days."));
        assert!(email.text.contains("Don’t know Marie? Ignore this email."));
        // User-provided values are HTML-escaped.
        assert!(email.html.contains("Acme &lt;Labs&gt;"));
        assert!(!email.html.contains("Acme <Labs>"));
        assert_eq!(email.illustration, INVITATION_PNG);

        let fr = payload(json!({
            "template": "invitation", "to": "paul@demo.test", "locale": "fr",
            "params": {"inviter": "Marie", "org": "Acme", "role": "admin", "link": "https://app.test/i", "days": 7}
        }));
        let email = render(&shared_catalog(), &fr).unwrap();
        assert!(email.text.contains("avec le rôle Admin."));
    }

    #[test]
    fn rejects_unknown_or_owner_role() {
        for role in ["owner", "admin_desc", "boss"] {
            let p = payload(json!({
                "template": "invitation", "to": "paul@demo.test", "locale": "en",
                "params": {"inviter": "Marie", "org": "Acme", "role": role, "link": "https://app.test/i", "days": 7}
            }));
            assert!(matches!(
                render(&shared_catalog(), &p),
                Err(EmailError::InvalidPayload(_))
            ));
        }
        let p = payload(json!({
            "template": "invitation", "to": "paul@demo.test", "locale": "en",
            "params": {"inviter": "Marie", "org": "Acme", "link": "https://app.test/i", "days": 7}
        }));
        assert!(render(&shared_catalog(), &p).is_err());
    }

    #[test]
    fn pro_welcome_has_no_expiry_nor_ignore_line() {
        let p = payload(json!({
            "template": "pro_welcome", "to": "marie@demo.test", "locale": "fr",
            "params": {"firstName": "Marie", "org": "Acme", "link": "http://localhost:5173/app/123"}
        }));
        let email = render(&shared_catalog(), &p).unwrap();
        assert_eq!(email.subject, "Acme passe en Pro");
        assert!(email.text.contains("formule Pro\u{a0}: projets"));
        assert!(!email.text.contains("valable"));
        assert!(!email.html.contains("font-size:13px"));
        assert!(
            email
                .text
                .ends_with("e-mail envoyé dans la boîte de test Mailpit.\n")
        );
        assert_eq!(email.illustration, PRO_PNG);
    }

    #[test]
    fn missing_key_fails_instead_of_sending_half_an_email() {
        let catalog = Catalog::default()
            .with_locale(
                "fr",
                r#"{"email.verify.subject": "Sujet", "email.verify.preheader": "P",
                    "email.verify.title": "T", "email.verify.body": "B {firstName}",
                    "email.verify.button": "Go", "email.verify.expiry": "{hours} h",
                    "email.footer": "F"}"#,
            )
            .unwrap();
        let err = render(&catalog, &verify_fr()).unwrap_err();
        assert!(matches!(
            err,
            EmailError::Text(TextError::MissingKey { ref key, .. }) if key == "email.verify.ignore"
        ));
        assert!(err.to_string().contains("email.verify.ignore"));
    }

    #[test]
    fn missing_param_or_locale_fails() {
        let mut p = verify_fr();
        p.params.remove("hours");
        assert!(matches!(
            render(&shared_catalog(), &p),
            Err(EmailError::Text(TextError::MissingParam { .. }))
        ));

        let mut p = verify_fr();
        p.locale = "de".into();
        assert!(matches!(
            render(&shared_catalog(), &p),
            Err(EmailError::Text(TextError::UnknownLocale(_)))
        ));
    }

    #[test]
    fn rejects_bad_links_and_addresses() {
        let mut p = verify_fr();
        p.params.remove("link");
        assert!(matches!(
            render(&shared_catalog(), &p),
            Err(EmailError::InvalidPayload(_))
        ));

        let mut p = verify_fr();
        p.params.insert("link".into(), json!("javascript:alert(1)"));
        assert!(matches!(
            render(&shared_catalog(), &p),
            Err(EmailError::InvalidPayload(_))
        ));

        let mut p = verify_fr();
        p.to = "not an address".into();
        assert!(matches!(
            render(&shared_catalog(), &p),
            Err(EmailError::InvalidAddress(_))
        ));
    }

    #[test]
    fn subject_never_contains_line_breaks() {
        let mut p = verify_fr();
        p.template = Template::Invitation;
        p.params = json!({"inviter": "Eve\r\nBcc: x@evil.test", "org": "Acme", "role": "member",
                          "link": "https://app.test/i", "days": 7})
        .as_object()
        .unwrap()
        .clone();
        let email = render(&shared_catalog(), &p).unwrap();
        assert!(!email.subject.contains('\n') && !email.subject.contains('\r'));
    }

    #[test]
    fn scalar_params_skip_objects() {
        let map = json!({"a": "x", "b": 2, "c": true, "d": null, "e": {"f": 1}});
        let params = scalar_params(map.as_object().unwrap());
        assert_eq!(params.len(), 3);
        assert_eq!(params["b"], "2");
        assert_eq!(params["c"], "true");
    }

    #[test]
    fn escapes_html_special_characters() {
        assert_eq!(
            escape_html(r#"<a href="x">Tom & 'Jerry'</a>"#),
            "&lt;a href=&quot;x&quot;&gt;Tom &amp; &#39;Jerry&#39;&lt;/a&gt;"
        );
    }

    #[test]
    fn builds_alternative_message_with_inline_images() {
        let email = render(&shared_catalog(), &verify_fr()).unwrap();
        let from: Mailbox = "LaunchKit <noreply@demo.test>".parse().unwrap();
        let raw = String::from_utf8(build_message(&from, &email).unwrap().formatted()).unwrap();
        assert!(raw.contains("multipart/alternative"));
        assert!(raw.contains("multipart/related"));
        assert!(raw.contains("text/plain"));
        assert!(raw.contains("text/html"));
        assert!(raw.contains("Content-ID: <logo>"));
        assert!(raw.contains("Content-ID: <illustration>"));
        assert!(raw.contains("To: marie@demo.test"));
        assert!(raw.contains("noreply@demo.test"));
    }

    #[test]
    fn mailer_validates_sender() {
        assert!(Mailer::new("localhost", 1025, "LaunchKit <noreply@demo.test>").is_ok());
        assert!(matches!(
            Mailer::new("localhost", 1025, "nobody"),
            Err(EmailError::InvalidAddress(_))
        ));
    }

    #[test]
    fn redacts_link_only() {
        let original = json!({
            "template": "verify_email", "to": "marie@demo.test", "locale": "fr",
            "params": {"firstName": "Marie", "link": "http://x/verify-email?token=secret", "hours": 24}
        });
        let redacted = redact_link(&original);
        assert_eq!(redacted["params"]["link"], json!("[envoyé]"));
        assert_eq!(redacted["params"]["firstName"], json!("Marie"));
        assert_eq!(redacted["to"], original["to"]);
        // Original untouched.
        assert_eq!(
            original["params"]["link"],
            json!("http://x/verify-email?token=secret")
        );
        // Nothing to redact: unchanged, no link added.
        let no_link = json!({"params": {"firstName": "Marie"}});
        assert_eq!(redact_link(&no_link), no_link);
        assert_eq!(
            redact_link(&json!({"paymentId": "1"})),
            json!({"paymentId": "1"})
        );
    }

    #[test]
    fn every_template_renders_in_every_shared_locale() {
        let catalog = shared_catalog();
        for locale in catalog.locales() {
            for (template, params) in [
                (
                    "verify_email",
                    json!({"firstName": "A", "link": "https://x/y", "hours": 24}),
                ),
                (
                    "invitation",
                    json!({"inviter": "A", "org": "B", "role": "admin", "link": "https://x/y", "days": 7}),
                ),
                (
                    "pro_welcome",
                    json!({"firstName": "A", "org": "B", "link": "https://x/y"}),
                ),
            ] {
                let p = payload(
                    json!({"template": template, "to": "a@demo.test", "locale": locale, "params": params}),
                );
                render(&catalog, &p).unwrap_or_else(|e| panic!("{template}/{locale}: {e}"));
            }
        }
    }
}
