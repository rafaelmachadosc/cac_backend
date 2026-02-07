# Script de Limpeza de Pastas Duplicadas
# Remove apenas pastas aninhadas duplicadas (svc/svc, publish/publish, etc.)
# Mantém a estrutura principal funcionando

Write-Host "=== LIMPEZA DE PASTAS DUPLICADAS ===" -ForegroundColor Cyan
Write-Host ""

$removedCount = 0
$errors = @()

# Função para remover pasta de forma segura
function Remove-DuplicateFolder {
    param([string]$Path)
    
    if (Test-Path $Path) {
        try {
            Write-Host "Removendo: $Path" -ForegroundColor Yellow
            Remove-Item -Path $Path -Recurse -Force -ErrorAction Stop
            $script:removedCount++
            return $true
        }
        catch {
            $script:errors += "Erro ao remover $Path : $_"
            Write-Host "  ERRO: $_" -ForegroundColor Red
            return $false
        }
    }
    return $false
}

# Limpar pastas duplicadas em publish/
Write-Host "Limpando pastas duplicadas em publish/..." -ForegroundColor Green
if (Test-Path "publish\publish") {
    Remove-DuplicateFolder "publish\publish"
}
if (Test-Path "publish\svc\svc") {
    Remove-DuplicateFolder "publish\svc\svc"
}
if (Test-Path "publish\svc\publish") {
    Remove-DuplicateFolder "publish\svc\publish"
}
if (Test-Path "publish\svc\svc\svc") {
    Remove-DuplicateFolder "publish\svc\svc\svc"
}
if (Test-Path "publish\svc\svc\publish") {
    Remove-DuplicateFolder "publish\svc\svc\publish"
}

# Limpar pastas duplicadas em svc/
Write-Host "Limpando pastas duplicadas em svc/..." -ForegroundColor Green
if (Test-Path "svc\svc") {
    Remove-DuplicateFolder "svc\svc"
}
if (Test-Path "svc\publish\publish") {
    Remove-DuplicateFolder "svc\publish\publish"
}
if (Test-Path "svc\publish\svc\svc") {
    Remove-DuplicateFolder "svc\publish\svc\svc"
}
if (Test-Path "svc\svc\svc") {
    Remove-DuplicateFolder "svc\svc\svc"
}
if (Test-Path "svc\svc\publish") {
    Remove-DuplicateFolder "svc\svc\publish"
}
if (Test-Path "svc\svc\svc\svc") {
    Remove-DuplicateFolder "svc\svc\svc\svc"
}

# Limpar pastas duplicadas em bin/ (build artifacts)
Write-Host "Limpando pastas duplicadas em bin/..." -ForegroundColor Green
$binPaths = @(
    "bin\Debug\net9.0\publish\publish",
    "bin\Debug\net9.0\publish\svc\svc",
    "bin\Debug\net9.0\svc\svc",
    "bin\Release\net9.0\publish\publish",
    "bin\Release\net9.0\publish\svc\svc",
    "bin\Release\net9.0\svc\svc"
)

foreach ($path in $binPaths) {
    if (Test-Path $path) {
        Remove-DuplicateFolder $path
    }
}

Write-Host ""
Write-Host "=== RESUMO ===" -ForegroundColor Cyan
Write-Host "Pastas removidas: $removedCount" -ForegroundColor Green

if ($errors.Count -gt 0) {
    Write-Host ""
    Write-Host "Erros encontrados:" -ForegroundColor Red
    foreach ($error in $errors) {
        Write-Host "  - $error" -ForegroundColor Red
    }
}
else {
    Write-Host "Limpeza concluída sem erros!" -ForegroundColor Green
}

Write-Host ""
Write-Host "NOTA: As pastas principais (publish/, svc/, bin/) foram mantidas." -ForegroundColor Yellow
Write-Host "Apenas as duplicadas aninhadas foram removidas." -ForegroundColor Yellow




