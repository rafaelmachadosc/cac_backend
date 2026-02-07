# Guia de Atualização do Servidor de Produção

## Problema Identificado
O servidor de produção está usando a chave incorreta (`caccoral` em vez de `cac-coral-drpaulo`), causando erro 404.

## Passos para Atualizar o Servidor

### 1. Compilar o Projeto para Produção

```powershell
cd AgendamentosApi
dotnet publish -c Release -o publish
```

Isso criará uma pasta `publish` com todos os arquivos necessários.

### 2. Verificar Arquivos Essenciais

Certifique-se de que estes arquivos estão na pasta `publish`:
- `AgendamentosApi.exe` (ou `AgendamentosApi.dll`)
- `appsettings.json` (com `ADMIN_KEY: "cac-coral-drpaulo"`)
- `appointments.db` (banco de dados SQLite)
- Pasta `Private/` com `admin.html`

### 3. Conectar ao Servidor de Produção

Use SSH, FTP ou o método de acesso que você usa para o servidor `caccoral.site`.

### 4. Fazer Backup do Servidor Atual

```bash
# Criar backup da aplicação atual
cp -r /caminho/da/aplicacao /caminho/da/aplicacao.backup.$(date +%Y%m%d)

# Backup do banco de dados
cp appointments.db appointments.db.backup.$(date +%Y%m%d)
```

### 5. Parar o Serviço Atual

```bash
# Se estiver rodando como serviço systemd
sudo systemctl stop agendamentos-api

# Ou se estiver rodando manualmente, encontre o processo e pare:
ps aux | grep AgendamentosApi
kill <PID>
```

### 6. Copiar Novos Arquivos

Copie todos os arquivos da pasta `publish` para o servidor, substituindo os arquivos antigos.

### 7. Verificar Configuração

Certifique-se de que o `appsettings.json` no servidor contém:
```json
{
  "ADMIN_KEY": "cac-coral-drpaulo",
  "ConnectionStrings": {
    "Default": "Data Source=appointments.db"
  }
}
```

### 8. Verificar Variáveis de Ambiente

Se houver variáveis de ambiente definidas no servidor, remova ou atualize:
```bash
# Verificar variáveis de ambiente
env | grep ADMIN_KEY

# Se existir e estiver incorreta, remover ou atualizar
unset ADMIN_KEY
# ou
export ADMIN_KEY="cac-coral-drpaulo"
```

### 9. Reiniciar o Serviço

```bash
# Se estiver rodando como serviço
sudo systemctl start agendamentos-api
sudo systemctl status agendamentos-api

# Ou iniciar manualmente
cd /caminho/da/aplicacao
dotnet AgendamentosApi.dll
# ou
./AgendamentosApi
```

### 10. Verificar se Está Funcionando

Teste os endpoints:
```bash
# Health check
curl http://localhost:5000/health

# Admin (deve funcionar agora)
curl "http://localhost:5000/admin?key=cac-coral-drpaulo"
```

### 11. Verificar Logs

```bash
# Se estiver usando systemd
sudo journalctl -u agendamentos-api -f

# Ou verificar logs da aplicação
tail -f logs/app.log
```

## Configuração do Servidor Web (Nginx/Apache)

Se estiver usando um servidor web reverso, verifique a configuração:

### Nginx
```nginx
server {
    listen 80;
    server_name caccoral.site;

    location / {
        proxy_pass http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection keep-alive;
        proxy_set_header Host $host;
        proxy_cache_bypass $http_upgrade;
    }
}
```

### Apache
```apache
<VirtualHost *:80>
    ServerName caccoral.site
    
    ProxyPreserveHost On
    ProxyPass / http://localhost:5000/
    ProxyPassReverse / http://localhost:5000/
</VirtualHost>
```

## Troubleshooting

### Se ainda der 404:
1. Verifique se o serviço está rodando: `ps aux | grep AgendamentosApi`
2. Verifique os logs para erros
3. Verifique se a porta está correta
4. Verifique se o firewall permite a porta
5. Teste localmente primeiro: `curl http://localhost:5000/admin?key=cac-coral-drpaulo`

### Se a chave não funcionar:
1. Verifique o `appsettings.json` no servidor
2. Verifique variáveis de ambiente: `env | grep ADMIN_KEY`
3. Verifique os logs para ver qual chave está sendo esperada

## Notas Importantes

- **SEMPRE faça backup antes de atualizar**
- **Teste localmente antes de implantar em produção**
- **A chave está hardcoded no código como `cac-coral-drpaulo` para garantir consistência**
- **O banco de dados `appointments.db` deve ser preservado durante a atualização**




