//! Godot Open MCP Hub — Tauri backend entry point.
//!
//! The Hub is a GUI over the `godot-open-mcp-cli` contracts: the `config`
//! modules mirror the CLI's install / setup-mcp / open / wait-for-ready /
//! status behavior (and the deterministic bridge-port formula) so the Hub
//! never diverges from the CLI. The Svelte frontend talks to these modules
//! exclusively through the [`commands`] surface.

pub mod commands;
pub mod config;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    // Best-effort log init. `RUST_LOG=info` surfaces the persistence /
    // detection spans during a repro.
    let _ = env_logger::Builder::from_env(env_logger::Env::default().default_filter_or("warn"))
        .try_init();

    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![
            // P11.1 — scaffold
            commands::load_settings,
            commands::save_settings,
            commands::load_projects,
            commands::add_project,
            commands::remove_project,
            commands::touch_project_opened,
            commands::resolve_bridge_port,
            commands::detect_project_kind,
            // P11.2 — AI Setup wizard
            commands::list_agents,
            commands::detect_project_state,
            commands::plan_mcp_config,
            commands::write_mcp_config,
            commands::install_addon,
            commands::launch_godot,
            commands::poll_bridge_ping,
            commands::clear_ai_setup,
            commands::copy_skill_files,
            // P11.3 — maintainer panel
            commands::read_mcp_package_info,
            commands::run_npm_script,
            commands::run_version_sync,
        ])
        .run(tauri::generate_context!())
        .expect("error while running godot-open-mcp-hub");
}
