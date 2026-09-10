use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    AppHandle, Emitter, Manager, Runtime,
};
use tauri_plugin_notification::NotificationExt;

const TRAY_ID: &str = "main-tray";

pub fn setup(app: &AppHandle) -> tauri::Result<()> {
    let icon = app
        .default_window_icon()
        .cloned()
        .expect("missing app icon for tray");

    let menu = build_menu(app)?;

    TrayIconBuilder::with_id(TRAY_ID)
        .icon(icon)
        .tooltip("nfa.pub tool")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(handle_menu_event)
        .on_tray_icon_event(handle_tray_event)
        .build(app)?;

    if let Some(window) = app.get_webview_window("main") {
        let w = window.clone();
        let handle = app.clone();
        window.on_window_event(move |event| {
            if matches!(
                event,
                tauri::WindowEvent::CloseRequested { .. } | tauri::WindowEvent::Moved(_)
            ) {
                crate::window_state::save(&w);
            }
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                api.prevent_close();
                let _ = w.hide();
                let _ = handle
                    .notification()
                    .builder()
                    .title("nfa.pub tool")
                    .body("Still running in the tray. Click the icon to reopen.")
                    .show();
            }
        });
    }

    Ok(())
}

pub fn rebuild(app: &AppHandle) -> tauri::Result<()> {
    if let Some(tray) = app.tray_by_id(TRAY_ID) {
        tray.set_menu(Some(build_menu(app)?))?;
    }
    Ok(())
}

fn build_menu<R: Runtime>(app: &AppHandle<R>) -> tauri::Result<Menu<R>> {
    let show = MenuItem::with_id(app, "show", "Show", true, None::<&str>)?;
    let import = MenuItem::with_id(app, "import", "Import", true, None::<&str>)?;
    let sep1 = PredefinedMenuItem::separator(app)?;
    let sep2 = PredefinedMenuItem::separator(app)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;

    let accounts = crate::steam::load_steam_accounts().unwrap_or_default();
    let streamer = crate::settings::load_settings().streamer_mode;
    let meta = crate::metadata::load();
    let now = crate::metadata::now_unix();
    let account_items: Vec<MenuItem<R>> = if accounts.is_empty() {
        vec![MenuItem::with_id(
            app,
            "no-accounts",
            "No accounts",
            false,
            None::<&str>,
        )?]
    } else {
        accounts
            .iter()
            .enumerate()
            .map(|(idx, acc)| {
                let mut label = if streamer {
                    if acc.most_recent {
                        format!("Account {} *", idx + 1)
                    } else {
                        format!("Account {}", idx + 1)
                    }
                } else if acc.most_recent {
                    format!("{} *", truncate_name(acc.display_name(), 28))
                } else {
                    truncate_name(acc.display_name(), 30)
                };
                if let Some(m) = meta.get(&acc.steamid) {
                    if m.on_cooldown(now) {
                        let left = m.cooldown_until.unwrap_or(now) - now;
                        label.push_str(&format!(
                            "  ({} left)",
                            crate::metadata::format_remaining(left)
                        ));
                    }
                }
                MenuItem::with_id(app, format!("signin:{}", acc.steamid), label, true, None::<&str>)
            })
            .collect::<tauri::Result<Vec<_>>>()?
    };

    let mut items: Vec<&dyn tauri::menu::IsMenuItem<R>> = vec![&show, &import, &sep1];
    for item in &account_items {
        items.push(item);
    }
    items.push(&sep2);
    items.push(&quit);

    Menu::with_items(app, &items)
}

fn truncate_name(name: &str, max: usize) -> String {
    if name.chars().count() <= max {
        name.to_string()
    } else {
        let trimmed: String = name.chars().take(max.saturating_sub(1)).collect();
        format!("{trimmed}\u{2026}")
    }
}

fn show_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window("main") {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

fn handle_menu_event(app: &AppHandle, event: tauri::menu::MenuEvent) {
    match event.id.as_ref() {
        "show" => show_window(app),
        "import" => {
            match crate::steam::import_from_clipboard() {
                Ok(msg) => {
                    let _ = rebuild(app);
                    let _ = app.emit("accounts-changed", ());
                    show_window(app);
                    let msg = if crate::settings::load_settings().streamer_mode {
                        "Account imported.".to_string()
                    } else {
                        msg
                    };
                    let _ = app.emit("status", msg);
                }
                Err(err) => {
                    show_window(app);
                    let _ = app.emit("status-error", err);
                }
            }
        }
        "quit" => app.exit(0),
        id if id.starts_with("signin:") => {
            let steamid = id.strip_prefix("signin:").unwrap_or("");
            if let Ok(accounts) = crate::steam::load_steam_accounts() {
                if let Some(account) = accounts.into_iter().find(|a| a.steamid == steamid) {
                    match crate::steam::handle_login_account(&account) {
                        Ok(_) => {
                            crate::metadata::touch_last_used(&account.steamid);
                            let _ = rebuild(app);
                            let _ = app.emit("accounts-changed", ());
                            let _ = app.emit("signed-in", account.steamid.clone());
                        }
                        Err(err) => {
                            let _ = app.emit("status-error", err);
                        }
                    }
                }
            }
        }
        _ => {}
    }
}

fn handle_tray_event(tray: &tauri::tray::TrayIcon, event: TrayIconEvent) {
    if let TrayIconEvent::Click {
        button: MouseButton::Left,
        button_state: MouseButtonState::Up,
        ..
    } = event
    {
        show_window(tray.app_handle());
    }
}
