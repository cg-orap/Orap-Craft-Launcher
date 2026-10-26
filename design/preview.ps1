# 把概念稿同步到 ASCII 目录并以开发服务器预览。
# 原因：项目位于中文路径，vite/esbuild 的 URL 解析会乱码，页面会全白。
param([switch]$NoServe)
$node = 'C:\Users\13634\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe'
$src = 'D:\repo\design'
$dst = 'D:\ui'

robocopy $src $dst /E /NFL /NDL /NJH /NJS /XD dist .vite node_modules 2>&1 | Out-Null
if (-not (Test-Path (Join-Path $dst 'node_modules\vite\bin\vite.js'))) {
  Write-Host '首次运行：正在安装依赖…'
  Push-Location $dst; & $node 'C:\Users\13634\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\pnpm\bin\pnpm.mjs' install --ignore-scripts; Pop-Location
}

if ($NoServe) { Write-Host '已同步到 D:\ui'; exit 0 }

Get-Process node -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddHours(-2) } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $node -ArgumentList 'D:\ui\node_modules\vite\bin\vite.js','--port','5273','--strictPort','--host','127.0.0.1' -WorkingDirectory $dst -WindowStyle Hidden -RedirectStandardOutput 'D:\gd\vite-out.log' -RedirectStandardError 'D:\gd\vite-err.log'
Start-Sleep -Seconds 8
Write-Host '预览：http://127.0.0.1:5273  （?page=instances / ?page=logs 可直接看单页）'