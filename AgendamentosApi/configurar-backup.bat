@echo off
echo ========================================
echo  CONFIGURAR BACKUP AUTOMATICO
echo ========================================
echo.
echo Este script precisa ser executado como Administrador!
echo.
echo Pressione qualquer tecla para continuar...
pause >nul

powershell -ExecutionPolicy Bypass -File "%~dp0setup-backup-schedule.ps1"

echo.
echo ========================================
echo  CONFIGURACAO CONCLUIDA
echo ========================================
echo.
echo Para verificar se foi criado, execute:
echo   Get-ScheduledTask -TaskName "AgendamentosApi_DailyBackup"
echo.
pause




