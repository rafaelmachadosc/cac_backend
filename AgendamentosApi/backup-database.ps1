# Script de Backup Automático do Banco de Dados SQLite
# Cria backup diário do arquivo appointments.db

param(
    [string]$BackupPath = "backups",
    [int]$RetentionDays = 30
)

Write-Host "=== BACKUP DO BANCO DE DADOS ===" -ForegroundColor Cyan
Write-Host ""

$dbFile = "appointments.db"
$timestamp = Get-Date -Format "yyyy-MM-dd_HH-mm-ss"
$backupFileName = "appointments_backup_$timestamp.db"
$backupFilePath = Join-Path $BackupPath $backupFileName

# Criar pasta de backups se não existir
if (-not (Test-Path $BackupPath)) {
    New-Item -ItemType Directory -Path $BackupPath -Force | Out-Null
    Write-Host "Pasta de backups criada: $BackupPath" -ForegroundColor Green
}

# Verificar se o arquivo do banco existe
if (-not (Test-Path $dbFile)) {
    Write-Host "ERRO: Arquivo $dbFile não encontrado!" -ForegroundColor Red
    exit 1
}

try {
    # Copiar arquivo do banco
    Copy-Item -Path $dbFile -Destination $backupFilePath -Force
    Write-Host "✅ Backup criado com sucesso: $backupFileName" -ForegroundColor Green
    
    # Obter tamanho do backup
    $backupSize = (Get-Item $backupFilePath).Length / 1MB
    Write-Host "   Tamanho: $([math]::Round($backupSize, 2)) MB" -ForegroundColor Gray
    
    # Limpar backups antigos (manter apenas os últimos X dias)
    Write-Host ""
    Write-Host "Limpando backups antigos (mantendo últimos $RetentionDays dias)..." -ForegroundColor Yellow
    
    $cutoffDate = (Get-Date).AddDays(-$RetentionDays)
    $oldBackups = Get-ChildItem -Path $BackupPath -Filter "appointments_backup_*.db" | 
        Where-Object { $_.LastWriteTime -lt $cutoffDate }
    
    $removedCount = 0
    foreach ($oldBackup in $oldBackups) {
        Remove-Item -Path $oldBackup.FullName -Force
        Write-Host "   Removido: $($oldBackup.Name)" -ForegroundColor Gray
        $removedCount++
    }
    
    if ($removedCount -gt 0) {
        Write-Host "✅ $removedCount backup(s) antigo(s) removido(s)" -ForegroundColor Green
    } else {
        Write-Host "   Nenhum backup antigo encontrado" -ForegroundColor Gray
    }
    
    # Listar backups atuais
    Write-Host ""
    Write-Host "=== BACKUPS DISPONÍVEIS ===" -ForegroundColor Cyan
    $allBackups = Get-ChildItem -Path $BackupPath -Filter "appointments_backup_*.db" | 
        Sort-Object LastWriteTime -Descending
    
    $totalSize = 0
    foreach ($backup in $allBackups) {
        $size = $backup.Length / 1MB
        $totalSize += $size
        $age = (Get-Date) - $backup.LastWriteTime
        $ageText = if ($age.Days -gt 0) { "$($age.Days) dia(s)" } else { "$($age.Hours) hora(s)" }
        Write-Host "  $($backup.Name) - $([math]::Round($size, 2)) MB - $ageText atrás" -ForegroundColor White
    }
    
    Write-Host ""
    Write-Host "Total: $($allBackups.Count) backup(s) - $([math]::Round($totalSize, 2)) MB" -ForegroundColor Green
    
    Write-Host ""
    Write-Host "✅ Backup concluído com sucesso!" -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "❌ ERRO ao criar backup: $_" -ForegroundColor Red
    exit 1
}




