//! Hub backend `config` module.
//!
//! Houses the deterministic bridge-port mirror, config persistence + project
//! inventory (P11.1), the MCP-client config writer + wizard detection/launch
//! (P11.2), and the maintainer npm command runner + project-kind detection
//! (P11.3). Each submodule mirrors a `godot-open-mcp-cli` contract so the Hub
//! never diverges from the CLI's behavior.

pub mod ai_toolkit;
pub mod bridge_port;
pub mod command_runner;
pub mod mcp_config;
pub mod paths;
pub mod persistence;
pub mod project_godot;
pub mod project_kind;
pub mod projects;
pub mod schemas;
pub mod wizard;
