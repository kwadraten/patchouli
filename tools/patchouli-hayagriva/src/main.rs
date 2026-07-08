use std::env;
use std::fmt::{self, Write as _};
use std::fs;
use std::process::ExitCode;

use hayagriva::citationberg::json::Item as CslJsonItem;
use hayagriva::citationberg::{
    Display, FontStyle, FontVariant, FontWeight, IndependentStyle, Locale,
    LocaleCode, LocaleFile, TextDecoration, VerticalAlign,
};
use hayagriva::{
    BibliographyDriver, BibliographyItem, BibliographyRequest, BufWriteFormat,
    CitationItem, CitationRequest, Elem, ElemChild, Formatted, Formatting,
    RenderedBibliography,
};
use serde::{Deserialize, Serialize};

const EN_US_LOCALE_XML: &str =
    include_str!("../../../src/Patchouli.Infrastructure/Csl/Resources/locales-en-US.xml");
const ZH_CN_LOCALE_XML: &str =
    include_str!("../../../src/Patchouli.Infrastructure/Csl/Resources/locales-zh-CN.xml");

fn main() -> ExitCode {
    match run() {
        Ok(response) => match serde_json::to_string(&response) {
            Ok(json) => {
                println!("{json}");
                ExitCode::SUCCESS
            }
            Err(error) => {
                eprintln!("Failed to serialize response: {error}");
                ExitCode::from(1)
            }
        },
        Err(error) => {
            eprintln!("{error}");
            ExitCode::from(1)
        }
    }
}

fn run() -> Result<RenderResponse, String> {
    let request_path = env::args()
        .nth(1)
        .ok_or_else(|| "Expected a single request file path argument.".to_string())?;
    let request_json = fs::read_to_string(&request_path)
        .map_err(|error| format!("Failed to read render request: {error}"))?;
    let request: RenderRequest = serde_json::from_str(&request_json)
        .map_err(|error| format!("Failed to parse render request: {error}"))?;

    if request.items.is_empty() {
        return Err("At least one bibliography item is required.".to_string());
    }

    let style = IndependentStyle::from_xml(&request.style_xml)
        .map_err(|error| format!("Failed to parse CSL style XML: {error}"))?;
    let locales = load_locales()?;
    let mut warnings = Vec::new();
    let locale = resolve_locale(request.locale.as_deref(), &mut warnings)?;

    let mut driver: BibliographyDriver<'_, CslJsonItem> = BibliographyDriver::new();
    driver.citation(CitationRequest::new(
        request.items.iter().map(CitationItem::with_entry).collect(),
        &style,
        locale.clone(),
        &locales,
        Some(1),
    ));

    let rendered = driver.finish(BibliographyRequest::new(&style, locale.clone(), &locales));
    let bibliography = rendered
        .bibliography
        .ok_or_else(|| "CSL engine returned no bibliography.".to_string())?;
    if bibliography.items.is_empty() {
        return Err("CSL engine returned an empty bibliography.".to_string());
    }

    let rendered_text = render_text(&bibliography)
        .map_err(|error| format!("Failed to render bibliography text: {error}"))?;
    let rendered_html = render_html(&bibliography)
        .map_err(|error| format!("Failed to render bibliography HTML: {error}"))?;
    if rendered_text.trim().is_empty() || rendered_html.trim().is_empty() {
        return Err("CSL engine returned empty bibliography output.".to_string());
    }

    Ok(RenderResponse {
        style_id: request.style_id,
        locale: locale.map(|value| value.to_string()),
        rendered_text,
        rendered_html,
        warnings,
        errors: Vec::new(),
    })
}

fn load_locales() -> Result<Vec<Locale>, String> {
    Ok(vec![
        LocaleFile::from_xml(EN_US_LOCALE_XML)
            .map_err(|error| format!("Failed to load en-US locale XML: {error}"))?
            .into(),
        LocaleFile::from_xml(ZH_CN_LOCALE_XML)
            .map_err(|error| format!("Failed to load zh-CN locale XML: {error}"))?
            .into(),
    ])
}

fn resolve_locale(
    locale: Option<&str>,
    warnings: &mut Vec<String>,
) -> Result<Option<LocaleCode>, String> {
    let Some(raw) = locale.map(str::trim).filter(|value| !value.is_empty()) else {
        return Ok(None);
    };

    if !matches!(raw, "en-US" | "zh-CN") {
        warnings.push(format!(
            "Locale '{raw}' is not bundled yet; falling back to the CSL style default locale."
        ));
        return Ok(None);
    }

    Ok(Some(LocaleCode(raw.to_string())))
}

fn render_text(bibliography: &RenderedBibliography) -> Result<String, fmt::Error> {
    let mut output = String::new();
    for (index, item) in bibliography.items.iter().enumerate() {
        if index > 0 {
            output.push('\n');
        }

        if let Some(prefix) = &item.first_field {
            prefix.write_buf(&mut output, BufWriteFormat::Plain)?;
            if !output.ends_with(char::is_whitespace) {
                output.push(' ');
            }
        }

        item.content.write_buf(&mut output, BufWriteFormat::Plain)?;
    }

    Ok(output.trim().to_string())
}

fn render_html(bibliography: &RenderedBibliography) -> Result<String, fmt::Error> {
    let mut output = String::new();
    output.push_str(r#"<div class="csl-bib-body">"#);
    for item in &bibliography.items {
        render_html_item(item, &mut output)?;
    }
    output.push_str("</div>");
    Ok(output)
}

fn render_html_item(item: &BibliographyItem, output: &mut String) -> Result<(), fmt::Error> {
    let mut second_field_align_suffix = "";
    output.push_str(r#"<div class="csl-entry">"#);
    if let Some(field) = &item.first_field {
        output.push_str(r#"<div class="csl-left-margin">"#);
        render_html_child(field, output)?;
        output.push_str(r#"</div><div class="csl-right-inline">"#);
        second_field_align_suffix = "</div>";
    }

    for child in &item.content.0 {
        render_html_child(child, output)?;
    }

    output.push_str(second_field_align_suffix);
    output.push_str("</div>");
    Ok(())
}

fn render_html_child(child: &ElemChild, output: &mut String) -> Result<(), fmt::Error> {
    match child {
        ElemChild::Text(formatted) => render_formatted_text(formatted, output),
        ElemChild::Elem(element) => render_html_elem(element, output),
        ElemChild::Link { text, url } => {
            output.push_str("<a href=\"");
            output.push_str(url);
            output.push_str("\">");
            render_formatted_text(text, output)?;
            output.push_str("</a>");
            Ok(())
        }
        other => other.write_buf(output, BufWriteFormat::Html),
    }
}

fn render_formatted_text(text: &Formatted, output: &mut String) -> Result<(), fmt::Error> {
    let formatting = text.formatting;
    if formatting == Formatting::default() {
        output.push_str(&text.text);
        return Ok(());
    }

    let mut css = String::new();
    let mut suffix = String::new();
    let mut push_elem = |start: &str, end: &str| {
        output.push_str(start);
        suffix.insert_str(0, end);
    };

    match formatting.vertical_align {
        VerticalAlign::Sub => push_elem("<sub>", "</sub>"),
        VerticalAlign::Sup => push_elem("<sup>", "</sup>"),
        VerticalAlign::Baseline => push_elem(r#"<span style="baseline">"#, "</span>"),
        VerticalAlign::None => {}
    }

    match formatting.font_weight {
        FontWeight::Bold if text.text.chars().any(|character| !character.is_whitespace()) => {
            push_elem("<b>", "</b>")
        }
        FontWeight::Light => css.push_str("font-weight:lighter;"),
        FontWeight::Normal | FontWeight::Bold => {}
    }

    if formatting.font_style == FontStyle::Italic {
        push_elem("<i>", "</i>");
    }

    if formatting.font_variant == FontVariant::SmallCaps {
        css.push_str("font-variant:small-caps;");
    }

    if formatting.text_decoration == TextDecoration::Underline {
        push_elem("<u>", "</u>");
    }

    if !css.is_empty() {
        write!(output, "<span style=\"{css}\">")?;
        suffix.insert_str(0, "</span>");
    }

    output.push_str(&text.text);
    output.push_str(&suffix);
    Ok(())
}

fn render_html_elem(element: &Elem, output: &mut String) -> Result<(), fmt::Error> {
    let mut div_suffix = "";
    if let Some(display) = element.display {
        div_suffix = "</div>";
        let div_class = match display {
            Display::Block => "csl-block",
            Display::LeftMargin => "csl-left-margin",
            Display::RightInline => "csl-right-inline",
            Display::Indent => "csl-indent",
        };

        write!(output, "<div class=\"{div_class}\">")?;
    }

    for child in &element.children.0 {
        render_html_child(child, output)?;
    }

    output.push_str(div_suffix);
    Ok(())
}

#[derive(Deserialize)]
struct RenderRequest {
    style_id: String,
    style_xml: String,
    locale: Option<String>,
    items: Vec<CslJsonItem>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RenderResponse {
    style_id: String,
    locale: Option<String>,
    rendered_text: String,
    rendered_html: String,
    warnings: Vec<String>,
    errors: Vec<String>,
}
