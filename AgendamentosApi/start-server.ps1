# Script para iniciar o servidor removendo variáveis de ambiente problemáticas

Write-Host "=== Limpando variáveis de ambiente problemáticas ===" -ForegroundColor Cyan

# Remover variáveis de ambiente problemáticas do processo atual
$env:ConnectionStrings__Default = $null
$env:ADMIN_KEY = $null
Write-Host "[OK] Variáveis de ambiente removidas do processo atual" -ForegroundColor Green

# Verificar se ainda existe
$check = [Environment]::GetEnvironmentVariable("ConnectionStrings__Default", "Process")
if ($check) {
    Write-Host "[WARNING] Variável ainda existe no processo: $check" -ForegroundColor Yellow
    [Environment]::SetEnvironmentVariable("ConnectionStrings__Default", $null, "Process")
    Write-Host "[OK] Variável removida forçadamente" -ForegroundColor Green
}

Write-Host "`n=== Iniciando servidor ===" -ForegroundColor Cyan
$env:ASPNETCORE_URLS = "http://0.0.0.0:5000"

Write-Host "URL: $env:ASPNETCORE_URLS" -ForegroundColor Green
Write-Host "Connection String será: Data Source=appointments.db (SQLite)" -ForegroundColor Green
Write-Host "`nIniciando dotnet run...`n" -ForegroundColor Yellow

dotnet run

