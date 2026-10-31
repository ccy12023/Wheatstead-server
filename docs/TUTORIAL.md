# 完整使用教程：从零跑起一台 Wheatstead 多人服务器

按顺序做一遍，大约 20 分钟。

## 0. 先看清边界

- 本仓库只有**控制服**。世界进程是游戏本体（`WheatsteadWorld --headless -- --server`），需要你自己的游戏构建。
- 官方发行版客户端把服务器地址写在工程设置里（`multiplayer/api_base`、`multiplayer/official_world_url`），要连自建服，需要能改这两项的构建。
- 支持的发行版由 `WHEATSTEAD_STEAM_APP_IDS` 决定，Demo 和正式版可以同时进；Playtest 也可以。

## 1. 准备

| 需要 | 说明 |
|---|---|
| Linux x64 | 控制服和世界进程都跑在 Linux 上 |
| .NET 8 SDK / 运行时 | 编译用 SDK，部署用 ASP.NET Core 运行时即可 |
| Python 3.10+ | 可选，只有开世界重置任务时需要 |
| Steamworks AppID | 正式版、Demo 各一个，填进配置 |
| 发行商 Web API Key | Steamworks →「用户与权限」→「管理 Web API 密钥」 |
| 游戏构建 | `WheatsteadWorld`，Linux 版 |
| 域名（可选） | 客户端要连你的服务器时用，后面会讲 |

## 2. 编译

```bash
git clone <你的仓库地址> Wheatstead-server
cd Wheatstead-server
dotnet publish src/Wheatstead.PlaytestServer.csproj -c Release -r linux-x64 --self-contained true -o out
```

`out/` 里会出现 `Wheatstead.PlaytestServer`。

## 3. 配置

复制模板，生成两个随机密钥：

```bash
cp .env.example .env
openssl rand -hex 32   # 填给 WHEATSTEAD_MULTIPLAYER_TOKEN_SECRET
openssl rand -hex 32   # 填给 WHEATSTEAD_NODE_HEARTBEAT_KEY
```

一个能跑起来的最小配置：

```ini
# 正式版 + Demo 都能进；换成你自己的 AppID
WHEATSTEAD_STEAM_APP_IDS=5485050,5489270

# Steamworks 发行商 Web API Key
WHEATSTEAD_STEAM_WEB_API_KEY=你的Key

# 入场令牌密钥，至少 32 字节，世界进程要用同一个值
WHEATSTEAD_MULTIPLAYER_TOKEN_SECRET=上一步生成的随机串

# 数据库（目录要可写）
WHEATSTEAD_MULTIPLAYER_DB=/var/lib/wheatstead/server/multiplayer.db
WHEATSTEAD_LEADERBOARD_DB=/var/lib/wheatstead/server/leaderboard.db

# 监听地址；反代后面就保持 127.0.0.1
WHEATSTEAD_LEADERBOARD_URLS=http://127.0.0.1:8081

# 世界进程走 Unix socket 连控制服（同机部署）
WHEATSTEAD_CONTROL_UNIX_SOCKET=/run/wheatstead/server.sock
WHEATSTEAD_NODE_HEARTBEAT_KEY=上一步生成的另一个随机串

# 首次建库的世界名
WHEATSTEAD_WORLD_NAME=My Wheatstead World
```

其他变量（家庭共享、令牌有效期、反馈邮件、管理密码）见 `.env.example`。

## 4. 启动控制服

```bash
set -a; . ./.env; set +a
./out/Wheatstead.PlaytestServer
```

看到这些行就对了：

```
[排行榜] 监听 http://127.0.0.1:8081
[多人] Steam 登录已启用，接受 AppID：5485050, 5489270
```

另开一个终端验证：

```bash
curl -s http://127.0.0.1:8081/v1/health
# {"ok":true,...,"steam_login_enabled":true}
```

首次启动会自动建库、建默认世界（`official-1`）。

## 5. 启动世界进程

同机部署时，世界进程通过这些环境变量找到控制服：

```bash
export WHEATSTEAD_CONTROL_UNIX_SOCKET=/run/wheatstead/server.sock
export WHEATSTEAD_CONTROL_UID=$(id -u wheatstead)          # 控制服进程的用户 uid
export WHEATSTEAD_NODE_HEARTBEAT_KEY=和 .env 里同一个值
export WHEATSTEAD_MULTIPLAYER_TOKEN_SECRET=和 .env 里同一个值
export WHEATSTEAD_ADMISSION_URL=http://127.0.0.1:8081
export WHEATSTEAD_WORLD_ID=official-1
export WHEATSTEAD_NODE_ID=node-1                            # 可选，节点标识
export WHEATSTEAD_NODE_NAME=main                            # 可选
export WHEATSTEAD_NODE_REGION=cn                            # 可选

./WheatsteadWorld --headless -- --server --port 8910
```

世界进程启动后会向控制服注册、上报地图并续租。用下面的命令确认：

```bash
curl -s http://127.0.0.1:8081/v1/multiplayer/worlds
# 世界里应该能看到你的世界，entry_ready 为 true
```

`entry_ready` 是 3 秒内的心跳状态；世界进程挂掉或重启控制服后，需要重启世界进程才会重新就绪。

## 6. 客户端接入

客户端需要能改这两项（改完重新导出）：

| 设置 | 值 |
|---|---|
| `multiplayer/api_base` | `https://你的域名` |
| `multiplayer/official_world_url` | `wss://你的域名/world`（或直连 `ws://服务器:8910`） |

玩家的流程：

1. 多人游戏 → 官方服务器
2. 用 Steam 账号登录（客户端取 Web API 票据）
3. 控制服校验票据和所有权，签发入场令牌
4. 选一块空地，带令牌连世界进程

Demo 和正式版走同一条流程，区别只在 AppID 和所有权校验（Demo 免费，只验票据）。

## 6.5 没有 Web API Key？用代验证模式

Web API Key 只有应用发行商能生成。如果你从项目作者那里拿到了「代验证服务」地址和令牌（加 QQ 3141995251 申请），就不需要 Key：

```ini
# 不填 WHEATSTEAD_STEAM_WEB_API_KEY
WHEATSTEAD_LICENSE_RELAY_URL=https://relay.example.com
WHEATSTEAD_LICENSE_RELAY_TOKEN=发行商发给你的令牌
```

启动后 health 里的 `steam_login_enabled` 仍然为 true，玩家的 Steam 票据会由你的控制服转发给中心服务验证，Key 不离开中心服务。

中心服务这边（发行商自己部署）：

```ini
WHEATSTEAD_STEAM_WEB_API_KEY=你的Key
WHEATSTEAD_STEAM_API_URL=https://partner.steam-api.com
WHEATSTEAD_LICENSE_RELAY_TOKEN=发给自建服运营者的令牌
WHEATSTEAD_LEADERBOARD_URLS=http://127.0.0.1:8081
WHEATSTEAD_MULTIPLAYER_DB=/var/lib/wheatstead/relay-m.db
WHEATSTEAD_LEADERBOARD_DB=/var/lib/wheatstead/relay-l.db
```

启用条件：`WHEATSTEAD_LICENSE_RELAY_TOKEN` 至少 32 字节。接口是 `POST /v1/license/verify`，只接受白名单里的 AppID，按 IP 每分钟 60 次限流。1 核 1G 的机器够跑，常驻内存约 100–150 MB。

## 7. 对外开放

推荐用 Caddy/Nginx 终止 TLS，只暴露 443：

- `/v1/*` 和 `/admin/*` 反代到 `127.0.0.1:8081`
- 世界端口 8910 可以单独反代成 `/world`（WebSocket），也可以直接放行 8910

Caddy 片段示例（按你的域名改）：

```caddy
example.com {
    handle /v1/* {
        reverse_proxy 127.0.0.1:8081
    }
    handle /admin* {
        reverse_proxy 127.0.0.1:8081
    }
    handle /world* {
        reverse_proxy 127.0.0.1:8910
    }
    handle {
        respond 404
    }
}
```

## 8. 管理后台

浏览器打开 `https://你的域名/admin`，密码是 `WHEATSTEAD_LEADERBOARD_ADMIN_PASSWORD`。

能看世界状态、在线玩家、地块归属，手动触发世界重置（需要 reset worker）。没设密码时后台登录关闭，接口本身仍然可用。

## 9. 常见报错

| 现象 | 原因 |
|---|---|
| health 里 `steam_login_enabled: false` | 没配 `WHEATSTEAD_STEAM_WEB_API_KEY` 或密钥长度不够 |
| 登录返回 `503 steam_login_disabled` | 同上 |
| `400 unsupported_appid` | 玩家的 AppID 不在 `WHEATSTEAD_STEAM_APP_IDS` 里 |
| `401 steam_ticket_rejected` | 票据过期、跨发行版票据，或客户端和服务器 AppID 对不上 |
| `403 steam_ownership_required` | 账号没有这个 App 的所有权，或家庭共享被禁用 |
| `409 world_unavailable` | 世界没开、世代不对，或 `entry_ready` 已过期 |
| 客户端连不上世界 | 8910 没放行，或 `official_world_url` 写错 |
| 世界进程日志报 socket 错误 | `WHEATSTEAD_CONTROL_UNIX_SOCKET` 路径不一致，或 `WHEATSTEAD_CONTROL_UID` 不是控制服进程的 uid |

## 10. 运维

- **systemd**：`deploy/wheatstead-server.service` 是控制服的单元示例；改成你的用户和路径，环境变量放 `/etc/wheatstead/server.env`（权限 600）。
- **备份**：SQLite 的 `multiplayer.db`、`leaderboard.db` 和 `-wal` 文件；直接复制或用 `sqlite3 .backup`。
- **升级**：`dotnet publish` 到新目录，切 systemd 的 `ExecStart`，`systemctl restart`；数据库有迁移脚本时先跑 `src/reset/migrate.py`。
- **日志**：直接看 stdout，或交给 journald。

## 11. 安全

- 令牌密钥和 Web API Key 只放环境变量，不要提交。
- 控制服只监听 `127.0.0.1`，靠反向代理对外。
- 管理后台建议再加一层 IP 白名单或反代 Basic Auth。
- 家庭共享默认关闭（`WHEATSTEAD_STEAM_ALLOW_FAMILY_SHARING=0`）。
- 世界重置是不可逆的，开 `WHEATSTEAD_RESET_WORKER_ENABLED=1` 前先确认备份。
