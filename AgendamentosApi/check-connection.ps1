# Script para verificar e limpar variáveis de ambiente relacionadas à connection string

Write-Host "=== Verificando variáveis de ambiente ===" -ForegroundColor Cyan

# Verificar variáveis de ambiente relacionadas
$envVars = @(
    "ConnectionStrings__Default",
    "ASPNETCORE_ConnectionStrings__Default",
    "DOTNET_ConnectionStrings__Default"
)

foreach ($var in $envVars) {
    $value = [Environment]::GetEnvironmentVariable($var, "User")
    if ($value) {
        Write-Host "[ENCONTRADO] $var = $value" -ForegroundColor Yellow
        Write-Host "  Esta variável pode estar causando o problema!" -ForegroundColor Red
        Write-Host "  Para remover, execute: [Environment]::SetEnvironmentVariable('$var', `$null, 'User')" -ForegroundColor Yellow
    } else {
        Write-Host "[OK] $var não está definida" -ForegroundColor Green
    }
}

# Verificar também no processo atual
Write-Host "`n=== Variáveis no processo atual ===" -ForegroundColor Cyan
foreach ($var in $envVars) {
    $value = Get-Item "Env:$var" -ErrorAction SilentlyContinue
    if ($value) {
        Write-Host "[ENCONTRADO] $var = $($value.Value)" -ForegroundColor Yellow
    }
}

Write-Host "`n=== Para limpar uma variável de ambiente ===" -ForegroundColor Cyan
Write-Host "Execute no PowerShell (como Administrador se necessário):" -ForegroundColor White
Write-Host "[Environment]::SetEnvironmentVariable('ConnectionStrings__Default', `$null, 'User')" -ForegroundColor Yellow
Write-Host "`nOu simplesmente reinicie o terminal e execute o projeto novamente." -ForegroundColor White





