$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Split-Path -Parent $src
$upstream = [IO.File]::ReadAllText((Join-Path $src 'upstream-reference.user.js'))
$zip = [IO.File]::ReadAllText((Join-Path $src 'jszip.min.js'))
$bridge = [IO.File]::ReadAllText((Join-Path $src 'bridge.js'))
$header = @'
// ==UserScript==
// @name         ChatGPT 全量备份助手 - EXE 连接组件
// @namespace    local.chatgpt.backup
// @version      1.0.1
// @description  配合本机备份助手导出全部对话；仅在由 EXE 打开的任务标签页工作
// @match        https://chatgpt.com/*
// @grant        GM_xmlhttpRequest
// @grant        GM_getValue
// @grant        GM_setValue
// @grant        GM_deleteValue
// @grant        unsafeWindow
// @connect      127.0.0.1
// @noframes
// @run-at       document-end
// @license      MIT
// ==/UserScript==
// Export engine: ChatGPT Exporter 2.38.3, copyright pionxzh (MIT).
// JSZip 3.9.1, copyright Stuart Knightley and contributors (MIT).
'@
$body = $upstream.Substring($upstream.IndexOf('(function(jszip,'))
$needle = "`n`tmain();"
if (($body.Split([string[]]@($needle),[StringSplitOptions]::None).Count - 1) -ne 1) { throw 'Pinned upstream main() location changed' }
$body = $body.Replace($needle, "`n`t// Desktop bridge replaces normal UI mounting.")
$end = '})(JSZip, window);'
if (!$body.Contains($end)) { throw 'Pinned upstream wrapper changed' }
$body = $body.Replace($end, $bridge + "`n" + $end)
# Give the engine its own bounded fetch wrapper without modifying ChatGPT's page fetch.
$body = $body.Replace('"use strict";', '"use strict"; var fetch = window.fetch.bind(window);')
$bundle = $header + "`nvar JSZip; (function(){var module={exports:{}};var exports=module.exports;`n" + $zip + "`nJSZip=module.exports;})();`n" + $body
$encoding = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $src 'bridge.user.js'),$bundle,$encoding)
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "/resource:$src\bridge.user.js,bridge.user.js" "/out:$dest\ChatGPT全量备份助手.exe" "$src\BackupApp.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Output 'Build complete'
