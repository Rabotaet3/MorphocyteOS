use std::path::PathBuf;
use std::process::Command;

#[path = "src/theme_icon.rs"]
mod theme_icon;

fn main() {
    println!("cargo:rerun-if-changed=ui/icons");
    println!("cargo:rerun-if-env-changed=ATLAS_LLVM_BIN");
    let target = std::env::var("TARGET").unwrap_or_default();
    let needs_admin = cfg!(all(feature = "native-router", not(feature = "isolated-check")));
    if target.ends_with("windows-msvc") && needs_admin {
        println!("cargo:rustc-link-arg-bin=morphocyte-atlas=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bin=morphocyte-atlas=/MANIFESTUAC:level='requireAdministrator' uiAccess='false'");
    }
    if !target.ends_with("windows-gnu") {
        return;
    }
    let Some(tools) = std::env::var_os("ATLAS_LLVM_BIN").map(PathBuf::from) else {
        assert!(!needs_admin, "Set ATLAS_LLVM_BIN to the LLVM resource tools or build via build.ps1.");
        return;
    };
    let output = PathBuf::from(std::env::var_os("OUT_DIR").expect("Cargo output directory"));
    let definition = output.join("atlas.rc");
    let resources = output.join("atlas.res");
    let object = output.join("atlas.obj");
    let mut icons = String::new();
    for id in 1..=9 {
        let icon = std::fs::canonicalize(format!("ui/icons/{id:02}.ico")).expect("Theme icon");
        let filename = icon.display().to_string().replace('\\', "/");
        let resource = theme_icon::theme_icon_id(id);
        icons.push_str(&format!("{resource} ICON \"{}\"\n", filename.trim_start_matches("//?/")));
    }
    if needs_admin {
        let manifest = output.join("atlas.manifest");
        std::fs::write(&manifest, r#"<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity name="MorphocyteOS.Atlas" version="1.0.0.0" type="win32"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security><requestedPrivileges>
      <requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
    </requestedPrivileges></security>
  </trustInfo>
</assembly>
"#).expect("Application manifest");
        let filename = manifest.display().to_string().replace('\\', "/");
        icons.push_str(&format!("1 24 \"{filename}\"\n"));
    }
    std::fs::write(&definition, icons).expect("Resource definition");
    let status = Command::new(tools.join("llvm-rc.exe"))
        .arg("/no-preprocess").arg("/fo").arg(&resources).arg(&definition)
        .status().expect("Run llvm-rc");
    assert!(status.success(), "Unable to compile theme icons");
    let status = Command::new(tools.join("llvm-cvtres.exe"))
        .arg("/machine:x64")
        .arg(format!("/out:{}", object.display()))
        .arg(&resources)
        .status().expect("Run llvm-cvtres");
    assert!(status.success(), "Unable to link theme icons");
    println!("cargo:rustc-link-arg-bin=morphocyte-atlas={}", object.display());
}
