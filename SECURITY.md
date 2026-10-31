# 安全

## 报告问题

发现漏洞请用 GitHub 的私密漏洞报告（Security → Report a vulnerability），不要开公开 issue。

## 部署注意

- `WHEATSTEAD_STEAM_WEB_API_KEY`、`WHEATSTEAD_MULTIPLAYER_TOKEN_SECRET`、`WHEATSTEAD_LICENSE_RELAY_TOKEN` 只放环境变量或权限 600 的 env 文件。
- 控制服只监听回环地址，公网入口交给 Caddy/Nginx 终止 TLS。
- 管理后台建议再加一层访问控制。
- 代验证令牌是共享密钥，泄漏后换掉并重启控制服。
