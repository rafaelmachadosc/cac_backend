# Script para configurar Migrations do EF Core
# Este script prepara o ambiente para usar migrations no futuro

Write-Host "=== CONFIGURAÇÃO DE MIGRATIONS EF CORE ===" -ForegroundColor Cyan
Write-Host ""

# Verificar se as ferramentas do EF Core estão instaladas
Write-Host "Verificando ferramentas do EF Core..." -ForegroundColor Yellow
$efTools = dotnet tool list -g | Select-String "dotnet-ef"

if (-not $efTools) {
    Write-Host "Instalando ferramentas do EF Core..." -ForegroundColor Yellow
    dotnet tool install --global dotnet-ef
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Erro ao instalar ferramentas do EF Core" -ForegroundColor Red
        exit 1
    }
    Write-Host "✅ Ferramentas instaladas com sucesso" -ForegroundColor Green
} else {
    Write-Host "✅ Ferramentas do EF Core já estão instaladas" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== INSTRUÇÕES PARA USAR MIGRATIONS ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "IMPORTANTE: O sistema atual usa EnsureCreated() para compatibilidade." -ForegroundColor Yellow
Write-Host "Para migrar para migrations no futuro:" -ForegroundColor Yellow
Write-Host ""
Write-Host "1. Criar migration inicial (baseada no banco atual):" -ForegroundColor White
Write-Host "   dotnet ef migrations add InitialCreate --project ." -ForegroundColor Gray
Write-Host ""
Write-Host "2. Aplicar migrations:" -ForegroundColor White
Write-Host "   dotnet ef database update --project ." -ForegroundColor Gray
Write-Host ""
Write-Host "3. No Program.cs, substituir EnsureCreated() por:" -ForegroundColor White
Write-Host "   await db.Database.MigrateAsync();" -ForegroundColor Gray
Write-Host ""
Write-Host "NOTA: Faça backup do banco antes de aplicar migrations!" -ForegroundColor Red
Write-Host ""