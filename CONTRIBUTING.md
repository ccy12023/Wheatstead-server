# 贡献

## 开发

```bash
dotnet build src/Wheatstead.PlaytestServer.csproj
dotnet run --project src/Wheatstead.PlaytestServer.csproj
```

启动前按 `.env.example` 配好环境变量；没有 Steam Key 时用代验证模式也能本地跑。

## 提交

- 改动尽量小，一次只解决一件事。
- 新增配置项写进 `.env.example` 和 `README.md` 的配置表。
- 提交前跑一遍 `dotnet build`，涉及接口的改动请说明验证方式。

## 行为

讨论保持具体：贴日志、复现步骤和版本，不用客气话。
