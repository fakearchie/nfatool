mod cache;
mod capture;
mod commands;
mod log;
mod metadata;
mod settings;
mod steam;
mod tray;
mod update;
mod vault;
mod window_state;

pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_notification::init())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .setup(|app| {
            tray::setup(app.handle())?;
            capture::apply_to_main(app.handle(), settings::load_settings().hide_from_capture);
            capture::strip_border_on_main(app.handle());
            window_state::restore(app.handle());
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::list_accounts,
            commands::fetch_account_intel,
            commands::validate_api_key,
            commands::read_clipboard,
            commands::import_clipboard,
            commands::import_account,
            commands::sign_in,
            commands::remove_account,
            commands::clear_steam,
            commands::get_settings,
            commands::save_settings,
            commands::get_metadata,
            commands::set_account_color,
            commands::set_account_cooldown,
            commands::import_from_file,
            commands::import_from_steam_cache,
            commands::export_tokens,
            commands::export_tokens_to_file,
            commands::copy_token,
            commands::copy_text,
            commands::open_profile,
            commands::prune_expired,
            commands::check_for_update,
            commands::install_update,
            commands::app_version,
            commands::open_url,
            commands::get_log,
            commands::clear_log,
            commands::watch_sign_in,
            commands::vault_status,
            commands::vault_unlock,
            commands::vault_lock,
            commands::vault_enable,
            commands::vault_disable,
            commands::vault_change
        ])
        .run(tauri::generate_context!())
        .expect("error while running nfa.pub tool");
}
