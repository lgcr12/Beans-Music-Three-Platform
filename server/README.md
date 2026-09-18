# Beans Account Server

Beans 账号服务只保存派生认证密钥的哈希、设备状态、密文保险库和密文同步记录。它不会代理 QQ 音乐、网易云音乐或任何音频流。

## 本地启动

1. 复制 `.env.example` 为 `.env`，至少修改 `POSTGRES_PASSWORD` 与 `BEANS_JWT_SIGNING_KEY`。
2. 在 `server` 目录执行 `docker compose up --build`。
3. 开发邮件在 `http://localhost:8025` 查看；API 由 Caddy 暴露。

生产环境必须配置真实域名、TLS、SMTP、数据库备份，并保持 `BEANS_EXPOSE_CODES=false`。

## 验证

```bash
dotnet test Beans.Api.Tests/Beans.Api.Tests.csproj
```

接口契约位于 `../contracts/openapi.yaml`。
