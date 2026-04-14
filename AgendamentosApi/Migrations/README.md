# Migrations do Banco de Dados

Este diretório contém as migrations do Entity Framework Core.

## Como usar

### Criar uma nova migration:
```bash
dotnet ef migrations add NomeDaMigration --project .
```

### Aplicar migrations pendentes:
```bash
dotnet ef database update --project .
```

### Reverter última migration:
```bash
dotnet ef database update NomeDaMigrationAnterior --project .
```

## Importante

- O banco de dados atual (appointments.db) já está funcionando
- As migrations são criadas para versionamento futuro
- O sistema continua usando `EnsureCreated()` para compatibilidade
- Migrations podem ser aplicadas quando necessário




