//! E-mail texts: flat `<locale>.json` files (`email.verify.subject`, …) with
//! simple ICU placeholders such as `{firstName}`.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Debug, thiserror::Error, PartialEq, Eq)]
pub enum TextError {
    #[error("unknown locale {0:?}")]
    UnknownLocale(String),
    #[error("missing text {key:?} for locale {locale:?}")]
    MissingKey { locale: String, key: String },
    #[error("text {key:?} needs parameter {param:?}")]
    MissingParam { key: String, param: String },
}

#[derive(Debug, thiserror::Error)]
pub enum LoadError {
    #[error("cannot read {path}: {source}")]
    Io {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("invalid JSON in {path}: {source}")]
    Json {
        path: PathBuf,
        source: serde_json::Error,
    },
    #[error("no <locale>.json file found in {0}")]
    Empty(PathBuf),
}

/// Texts for every locale found in the i18n directory.
#[derive(Debug, Default, Clone)]
pub struct Catalog {
    locales: HashMap<String, HashMap<String, String>>,
}

impl Catalog {
    /// Loads every `<locale>.json` file of `dir`. Non-string values are ignored.
    pub fn load_dir(dir: &Path) -> Result<Self, LoadError> {
        let io = |source| LoadError::Io {
            path: dir.to_owned(),
            source,
        };
        let mut catalog = Self::default();
        for entry in std::fs::read_dir(dir).map_err(io)? {
            let path = entry.map_err(io)?.path();
            if path.extension().and_then(|e| e.to_str()) != Some("json") {
                continue;
            }
            let Some(locale) = path.file_stem().and_then(|s| s.to_str()) else {
                continue;
            };
            let raw = std::fs::read_to_string(&path).map_err(|source| LoadError::Io {
                path: path.clone(),
                source,
            })?;
            let texts = parse_texts(&raw).map_err(|source| LoadError::Json {
                path: path.clone(),
                source,
            })?;
            catalog.locales.insert(locale.to_owned(), texts);
        }
        if catalog.locales.is_empty() {
            return Err(LoadError::Empty(dir.to_owned()));
        }
        Ok(catalog)
    }

    /// Adds a locale from a JSON string (tests and tools).
    pub fn with_locale(mut self, locale: &str, json: &str) -> Result<Self, serde_json::Error> {
        self.locales.insert(locale.to_owned(), parse_texts(json)?);
        Ok(self)
    }

    pub fn has_locale(&self, locale: &str) -> bool {
        self.locales.contains_key(locale)
    }

    /// Sorted list of loaded locales.
    pub fn locales(&self) -> Vec<&str> {
        let mut locales: Vec<&str> = self.locales.keys().map(String::as_str).collect();
        locales.sort_unstable();
        locales
    }

    /// Raw text for `key`.
    pub fn text(&self, locale: &str, key: &str) -> Result<&str, TextError> {
        let texts = self
            .locales
            .get(locale)
            .ok_or_else(|| TextError::UnknownLocale(locale.to_owned()))?;
        texts
            .get(key)
            .map(String::as_str)
            .ok_or_else(|| TextError::MissingKey {
                locale: locale.to_owned(),
                key: key.to_owned(),
            })
    }

    /// Text for `key` with its placeholders replaced by `params`.
    pub fn render(
        &self,
        locale: &str,
        key: &str,
        params: &HashMap<String, String>,
    ) -> Result<String, TextError> {
        format(self.text(locale, key)?, params).map_err(|param| TextError::MissingParam {
            key: key.to_owned(),
            param,
        })
    }
}

fn parse_texts(raw: &str) -> Result<HashMap<String, String>, serde_json::Error> {
    let values: HashMap<String, serde_json::Value> = serde_json::from_str(raw)?;
    Ok(values
        .into_iter()
        .filter_map(|(k, v)| match v {
            serde_json::Value::String(s) => Some((k, s)),
            _ => None,
        })
        .collect())
}

/// Replaces `{name}` placeholders. Any other brace is kept as is.
/// Returns the name of the first placeholder without a value.
pub fn format(template: &str, params: &HashMap<String, String>) -> Result<String, String> {
    let mut out = String::with_capacity(template.len());
    let mut rest = template;
    while let Some(start) = rest.find('{') {
        out.push_str(&rest[..start]);
        let after = &rest[start + 1..];
        let name_len = after
            .find(|c: char| !(c.is_ascii_alphanumeric() || c == '_'))
            .unwrap_or(after.len());
        if name_len > 0 && after[name_len..].starts_with('}') {
            let name = &after[..name_len];
            let value = params.get(name).ok_or_else(|| name.to_owned())?;
            out.push_str(value);
            rest = &after[name_len + 1..];
        } else {
            out.push('{');
            rest = after;
        }
    }
    out.push_str(rest);
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn params(pairs: &[(&str, &str)]) -> HashMap<String, String> {
        pairs
            .iter()
            .map(|(k, v)| ((*k).to_owned(), (*v).to_owned()))
            .collect()
    }

    #[test]
    fn replaces_placeholders() {
        let out = format(
            "{inviter} invited you to join {org}",
            &params(&[("inviter", "Paul"), ("org", "Acme")]),
        )
        .unwrap();
        assert_eq!(out, "Paul invited you to join Acme");
    }

    #[test]
    fn keeps_non_placeholder_braces_and_unicode() {
        let out = format("{ x } {} {a-b} é {n}", &params(&[("n", "1")])).unwrap();
        assert_eq!(out, "{ x } {} {a-b} é 1");
        assert_eq!(format("trailing {", &params(&[])).unwrap(), "trailing {");
    }

    #[test]
    fn reports_missing_param() {
        assert_eq!(
            format("Hi {firstName}", &params(&[])).unwrap_err(),
            "firstName"
        );
    }

    #[test]
    fn preserves_non_breaking_spaces() {
        let catalog = Catalog::default()
            .with_locale(
                "fr",
                r#"{"k": "Vous ne connaissez pas {inviter} ? Ignorez."}"#,
            )
            .unwrap();
        let out = catalog
            .render("fr", "k", &params(&[("inviter", "Paul")]))
            .unwrap();
        assert_eq!(out, "Vous ne connaissez pas Paul\u{a0}? Ignorez.");
    }

    #[test]
    fn missing_key_and_locale_are_errors() {
        let catalog = Catalog::default()
            .with_locale("en", r#"{"a": "A", "n": 3}"#)
            .unwrap();
        assert!(catalog.has_locale("en"));
        assert_eq!(catalog.text("en", "a").unwrap(), "A");
        assert_eq!(
            catalog.text("en", "n").unwrap_err(),
            TextError::MissingKey {
                locale: "en".into(),
                key: "n".into()
            }
        );
        assert_eq!(
            catalog.text("de", "a").unwrap_err(),
            TextError::UnknownLocale("de".into())
        );
        let err = catalog
            .with_locale("en", r#"{"b": "{x}"}"#)
            .unwrap()
            .render("en", "b", &params(&[]))
            .unwrap_err();
        assert_eq!(
            err,
            TextError::MissingParam {
                key: "b".into(),
                param: "x".into()
            }
        );
    }

    #[test]
    fn loads_shared_i18n_files() {
        let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../shared/i18n");
        let catalog = Catalog::load_dir(&dir).unwrap();
        assert_eq!(catalog.locales(), vec!["en", "fr"]);
        assert_eq!(
            catalog.text("fr", "email.verify.subject").unwrap(),
            "Confirmez votre adresse e-mail"
        );
    }

    #[test]
    fn load_errors_are_reported() {
        let missing = Path::new("/nonexistent/launchkit-i18n");
        assert!(matches!(
            Catalog::load_dir(missing),
            Err(LoadError::Io { .. })
        ));

        let dir = std::env::temp_dir().join(format!("lk-i18n-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("notes.txt"), "ignored").unwrap();
        assert!(matches!(Catalog::load_dir(&dir), Err(LoadError::Empty(_))));
        std::fs::write(dir.join("fr.json"), "{not json").unwrap();
        let err = Catalog::load_dir(&dir).unwrap_err();
        assert!(matches!(err, LoadError::Json { .. }));
        assert!(err.to_string().contains("fr.json"));
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
