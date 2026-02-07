# 📋 Como Configurar Backup Automático

## ⚠️ IMPORTANTE: Executar como Administrador

O backup automático precisa ser configurado com privilégios de Administrador.

## 📝 Passos para Configurar

### Opção 1: Via PowerShell (Recomendado)

1. **Abrir PowerShell como Administrador:**
   - Pressione `Win + X`
   - Selecione "Windows PowerShell (Admin)" ou "Terminal (Admin)"

2. **Navegar até a pasta do projeto:**
   ```powershell
   cd "C:\Users\Rafael Machado\Desktop\AgendamentosProjeto\AgendamentosApi"
   ```

3. **Executar o script de configuração:**
   ```powershell
   .\setup-backup-schedule.ps1
   ```

4. **Verificar se foi criado:**
   ```powershell
   Get-ScheduledTask -TaskName "AgendamentosApi_DailyBackup"
   ```

### Opção 2: Via Interface Gráfica (Task Scheduler)

1. Abrir **Agendador de Tarefas** (Task Scheduler)
2. Clicar em **Criar Tarefa Básica**
3. Nome: `AgendamentosApi_DailyBackup`
4. Trigger: **Diariamente**
5. Horário: **23:59**
6. Ação: **Iniciar um programa**
   - Programa: `powershell.exe`
   - Argumentos: `-ExecutionPolicy Bypass -File "C:\Users\Rafael Machado\Desktop\AgendamentosProjeto\AgendamentosApi\backup-database.ps1"`
7. Marcar: **Executar com privilégios mais altos**
8. Finalizar

## ✅ Configuração Atual

- **Horário:** Diário às 23:59
- **Retenção:** 30 dias (backups antigos são removidos automaticamente)
- **Pasta:** `backups/` (criada automaticamente)
- **Formato:** `appointments_backup_YYYY-MM-DD_HH-mm-ss.db`

## 🧪 Testar Backup Manualmente

Para testar se o backup funciona antes de agendar:

```powershell
.\backup-database.ps1
```

## 📊 Verificar Backups

```powershell
Get-ChildItem backups\appointments_backup_*.db | Sort-Object LastWriteTime -Descending
```

## 🔧 Alterar Configurações

Para alterar horário ou retenção, edite o arquivo `setup-backup-schedule.ps1` e execute novamente.




