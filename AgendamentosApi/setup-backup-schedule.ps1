# Script para configurar agendamento automático de backup
# Executa backup diário às 02:00 AM

Write-Host "=== CONFIGURAÇÃO DE BACKUP AUTOMÁTICO ===" -ForegroundColor Cyan
Write-Host ""

$scriptPath = Join-Path $PSScriptRoot "backup-database.ps1"
$taskName = "AgendamentosApi_DailyBackup"

# Verificar se o script de backup existe
if (-not (Test-Path $scriptPath)) {
    Write-Host "ERRO: Script de backup não encontrado: $scriptPath" -ForegroundColor Red
    exit 1
}

try {
    # Verificar se a tarefa já existe
    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    
    if ($existingTask) {
        Write-Host "Tarefa já existe. Atualizando..." -ForegroundColor Yellow
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    
    # Criar ação (executar script PowerShell)
    $action = New-ScheduledTaskAction -Execute "PowerShell.exe" `
        -Argument "-ExecutionPolicy Bypass -File `"$scriptPath`""
    
    # Criar trigger (diário às 23:59)
    $trigger = New-ScheduledTaskTrigger -Daily -At "23:59"
    
    # Configurações da tarefa
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -RunOnlyIfNetworkAvailable:$false
    
    # Registrar tarefa
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Description "Backup diário automático do banco de dados AgendamentosApi" `
        -User "SYSTEM" `
        -RunLevel Highest
    
    Write-Host "✅ Tarefa agendada criada com sucesso!" -ForegroundColor Green
    Write-Host "   Nome: $taskName" -ForegroundColor Gray
    Write-Host "   Horário: Diário às 23:59" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Para executar manualmente:" -ForegroundColor Yellow
    Write-Host "   .\backup-database.ps1" -ForegroundColor White
    Write-Host ""
    Write-Host "Para verificar a tarefa:" -ForegroundColor Yellow
    Write-Host "   Get-ScheduledTask -TaskName '$taskName'" -ForegroundColor White
}
catch {
    Write-Host "❌ ERRO ao configurar agendamento: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "NOTA: Execute este script como Administrador!" -ForegroundColor Yellow
    exit 1
}

