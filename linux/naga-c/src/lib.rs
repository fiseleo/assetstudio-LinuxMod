//! WGSL to SPIR-V (naga) behind a C interface.
//!
//! `naga_wgsl_to_spirv` parses and validates a WGSL module and writes the SPIR-V of one entry point (SPIR-V 1.0,
//! debug names kept, no coordinate adjustment: the caller's projection deals with WebGPU's Y). On success it returns
//! 0 and the words, on failure 1 and a message; both are freed with `naga_free`.

use std::ffi::{c_char, CStr, CString};
use std::ptr;

fn compile(source: &str, stage: u32, entry: &str) -> Result<Vec<u32>, String> {
    let module = naga::front::wgsl::parse_str(source).map_err(|e| e.emit_to_string(source))?;
    let info = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::all())
        .validate(&module)
        .map_err(|e| e.emit_to_string(source))?;
    let shader_stage = match stage {
        0 => naga::ShaderStage::Vertex,
        1 => naga::ShaderStage::Fragment,
        _ => return Err(format!("unknown stage {stage}")),
    };
    let options = naga::back::spv::Options {
        lang_version: (1, 0),
        flags: naga::back::spv::WriterFlags::DEBUG | naga::back::spv::WriterFlags::LABEL_VARYINGS,
        ..Default::default()
    };
    let pipeline = naga::back::spv::PipelineOptions { shader_stage, entry_point: entry.to_string() };
    naga::back::spv::write_vec(&module, &info, &options, Some(&pipeline)).map_err(|e| e.to_string())
}

/// Compiles an entry point (stage 0 vertex, 1 fragment) of a WGSL module to SPIR-V.
#[no_mangle]
pub unsafe extern "C" fn naga_wgsl_to_spirv(source: *const c_char, stage: u32, entry: *const c_char,
    out_words: *mut *mut u32, out_count: *mut usize, out_error: *mut *mut c_char) -> i32 {
    *out_words = ptr::null_mut();
    *out_count = 0;
    *out_error = ptr::null_mut();
    let result = match (CStr::from_ptr(source).to_str(), CStr::from_ptr(entry).to_str()) {
        (Ok(source), Ok(entry)) => compile(source, stage, entry),
        _ => Err("the source or the entry point is not UTF-8".to_string()),
    };
    match result {
        Ok(words) => {
            let mut words = words.into_boxed_slice();
            *out_count = words.len();
            *out_words = words.as_mut_ptr();
            std::mem::forget(words);
            0
        }
        Err(message) => {
            *out_error = CString::new(message.replace('\0', " ")).unwrap_or_default().into_raw();
            1
        }
    }
}

/// Frees the words (with their count) or the message of `naga_wgsl_to_spirv`.
#[no_mangle]
pub unsafe extern "C" fn naga_free(words: *mut u32, count: usize, message: *mut c_char) {
    if !words.is_null() {
        drop(Box::from_raw(ptr::slice_from_raw_parts_mut(words, count)));
    }
    if !message.is_null() {
        drop(CString::from_raw(message));
    }
}
