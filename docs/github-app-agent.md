# GitHub App + Actions：逐步配置 M7A 机器人

本阶段接通机器人：维护者在 Issue / PR 评论 `/m7a ...`，Actions 运行 OpenCode，
通过你自己的 GitHub App 回复、修改代码和创建 PR。支持手动检查配置和手动下发任务。
无需服务器、公共 OpenCode App 或额外 Python 脚本。

`11.m7a_agent.yml` 负责通用机器人。定时同步、rebase、AI 审核和发布已拆到独立的
`12.upstream_sync.yml`，配置和演练步骤见 [官方 Release 自动同步](upstream-sync.md)。
定时入口需要单独启用 `UPSTREAM_SYNC_ENABLED`，不会因接通评论机器人而自动开启。

## 1. 创建自己的 GitHub App

打开 <https://github.com/settings/apps/new>，填写：

| 项目 | 设置 |
| --- | --- |
| GitHub App name | 例如 `fluoxue-m7a-bot`，名称需要全局唯一 |
| Homepage URL | `https://github.com/FLuoXue/March7thAssistant` |
| Callback URL / Setup URL | 留空 |
| Request user authorization (OAuth) during installation | 不勾选 |
| Enable Device Flow | 不勾选 |
| Webhook → Active | **取消勾选**；事件由 Actions 的 `on:` 接收 |
| Where can this GitHub App be installed? | Only on this account |

Repository permissions 配置：

| 权限 | 级别 | 用途 |
| --- | --- | --- |
| Contents | Read and write | 读取代码，推送机器人分支 |
| Issues | Read and write | 读取讨论、回复评论 |
| Pull requests | Read and write | 创建、更新 PR 和回复评审 |
| Variables | Read-only | 运行时读取 Variables 中的 `AI_API_KEY` |
| Metadata | Read-only | 仓库信息和维护者权限检查，通常自动包含 |

如果 API Key 使用 Secret，Variables 权限可以省略。其余权限保持 No access，
此阶段不授予 Workflows 写入或 Administration 权限。

点击 **Create GitHub App**。记录页面上的 **Client ID**（不是 Client secret）。
在 Private keys 区域点击 **Generate a private key**，下载 `.pem` 文件。

## 2. 安装到 fork

在 App 设置左侧点击 **Install App** → 选择你的账户 → **Only select repositories**，
只选择 `FLuoXue/March7thAssistant`，点击 Install。

创建 App 和安装 App 是两个步骤，必须都完成。以后增加 App 权限时，也需要确认安装权限更新。

## 3. 填入仓库配置

打开仓库 **Settings → Secrets and variables → Actions**。

在 **Variables** 中添加：

| 名称 | 值 |
| --- | --- |
| `BOT_APP_CLIENT_ID` | 第 1 步记录的 Client ID |
| `BOT_ENABLED` | `true`，允许评论触发；`false` 可暂停评论入口 |
| `AI_BASE_URL` | 供应商 API Base URL，例如 `https://api.example.com/v1` |
| `AI_MODEL_ID` | 供应商要求的模型 ID，必须支持工具调用 |
| `AI_API_KEY` | 你的模型 API Key，按你的选择支持放入 Variables |
| `AI_API_FORMAT` | 默认为 `chat-completions`，见下表 |

`AI_BASE_URL` 填 SDK 所需的基础地址，不要拼接 `/chat/completions`、`/responses` 或 `/messages`，
也不要在 URL 中附加 Key。是否保留 `/v1` 以供应商文档为准。

| `AI_API_FORMAT` | 适用协议 |
| --- | --- |
| `chat-completions` | Chat Completions 兼容接口，默认值 |
| `responses` | Responses 兼容接口 |
| `anthropic` | Anthropic Messages 兼容接口 |

在 **Secrets** 中添加：

| 名称 | 值 |
| --- | --- |
| `BOT_APP_PRIVATE_KEY` | `.pem` 文件的完整内容，包含 BEGIN / END 行 |

也可以把 `AI_API_KEY` 放在 Secrets；同名 Secret 优先于 Variable。
Variables 不是保密存储，拥有相应仓库权限的人能读取；工作流通过 API 在运行时取出 Key 后立即遮蔽日志，
不会直接把 `${{ vars.AI_API_KEY }}` 展开到命令或步骤环境中。不要开启包含敏感请求的调试输出。

## 4. 提交工作流到 fork 的 main

需要提交的文件：

* `.github/workflows/11.m7a_agent.yml`
* `docs/github-app-agent.md`

本仓库的远程命名是 **`fork` = 你的仓库，`origin` = 官方仓库**。
确认工作区变更后，在仓库目录执行：

```powershell
git add .github/workflows/11.m7a_agent.yml docs/github-app-agent.md
git commit -m "ci: add GitHub App coding agent"
git push fork main
```

工作流必须存在于默认分支，Issue 评论才会触发它。
如果 fork 的 Actions 尚未启用，在仓库 Actions 页面先启用。
如果 Settings → Actions → General 限制了第三方 Actions，允许本工作流使用的 actions、
astral-sh/setup-uv 和 anomalyco/opencode/github。

默认 `GITHUB_TOKEN` 保持只读即可；写入使用你自己的 App 安装令牌。
无需为了机器人把默认工作流权限整体改为 Read and write。

## 5. 先运行不调用模型的检查

打开 **Actions → M7A Agent → Run workflow**：

1. Branch 选择 `main`。
2. mode 选择 `check`，prompt 留空。
3. 点击 Run workflow。

成功后 Summary 会显示 `M7A Agent configuration verified`。
这一步检查维护者身份、App 认证、安装实际授予的 Contents / Issues / Pull requests 写权限、
仓库变量和 Key 是否存在，不调用模型、不修改代码、不消耗模型额度。
它还会向模型 API 路径发送一次不带凭据、模型或提示词的空请求，识别误填网页地址的情况。
该探测收到 401/403 并不代表配置失败：没有发送 Key，认证失败是预期行为。
Summary 同时列出 App 设置与安装授权；修改 App 设置后，安装授权可能仍在等待账户所有者接受更新。
绿色结果不代表 API Key、模型 ID 已得到供应商验证；下一步才是真实模型调用。

## 6. 验证模型和机器人回复

新建一个测试 Issue，在**评论**中输入（不是 Issue 标题或正文）：

```text
/m7a 阅读 assets/docs/DesktopSession.md，用中文概括桌面分身的使用方法。只回复，不修改文件。
```

预期结果：Actions 出现一次 M7A Agent 运行，随后你的 `<App 名称>[bot]` 回复。
首次运行需要下载 Agent 和 Python 环境，可能需要数分钟。

只有有仓库 write / maintain / admin 权限的账户可以发出指令。
普通访客和机器人发出的评论不会启动带凭据的 Agent。

## 7. 验证创建和继续修改 PR

在测试 Issue 下再评论：

```text
/m7a 新增 docs/agent-smoke-test.md，写明这是机器人接入测试，并链接现有桌面分身说明。不要修改其他文件。
```

预期结果：机器人新建分支、提交文件、创建 PR。你可以关闭这个测试 PR，无需合并。
在该 PR 中继续评论：

```text
/m7a 在刚才的测试文档中补充一句：桌面分身仅支持 Windows。
```

预期结果：机器人更新同一个 PR。代码行上的评审评论也支持 `/m7a ...`。
当前只接受本仓库分支的开放 PR；外部 fork PR、默认分支和 `bot/upstream-` 开头的同步候选分支不走此入口。

也可以从 Actions 手动运行：mode 选择 `agent`，prompt 填任务。
手动任务的解释结果在 Actions 日志中；产生代码改动时创建 PR。

## 8. 测试、审核与运行边界

机器人运行在 Ubuntu，可以执行 Python 测试；项目的原生 Windows 宿主检查由现有 Windows CI 负责。
本阶段不自动合并 PR、不创建标签、不发版、不改 Mirror酱。整个 job 最长 35 分钟，
其中 Agent 步骤单独限制为 **5 分钟**。配置 `default_agent: m7a-maintainer`，
让 v1.18.34 的 GitHub 集成实际使用自定义 Agent；仅设置 Action 的 `agent` 输入在此版本不足以生效。
Agent 的 `steps: 20` 是要求达到该轮数后收尾的软限制，硬停止由步骤超时保证。
这些不是供应商费用的硬上限，费用限额需要在供应商侧配置。

OpenCode 的会话公开分享被关闭。Action 入口固定为 `v1.18.34`；它的官方安装步骤仍会下载当前稳定 CLI，
因此并非整个 Agent 依赖链都锁定版本。

Agent 中的行为提示不能替代 GitHub 权限控制。建议 main 的 ruleset 要求通过 PR 更新，
至少一次人工审核，并且不要把此 App 放进 bypass 列表。此阶段 App 不需要改写 main 历史的权限。
在后续添加受控 rebase 发布任务时，再独立配置那个发布身份和规则。

## 独立的 rebase 同步流程

同步任务见 [配置说明](upstream-sync.md)。以下功能由独立工作流实现，不通过 `/m7a` 评论执行：

1. 定时检测 `moesnow/March7thAssistant` 的正式 Release，固定 tag 对应的 SHA。
2. 保存 fork 当前 main SHA，在 `bot/upstream-...` 隔离分支执行 rebase，保留桌面分身改动。
3. 无冲突走完整验证；有冲突调用 Agent，保留修改说明、失败日志和最终候选 SHA。
4. Python 测试、翻译校验、Windows 原生宿主检查和打包必须针对同一个候选提交。
5. 无 AI 修改可自动晋升；AI 修改必须审批后晋升，审批绑定候选 SHA。
6. 先保存旧 main 的备份引用，再用 `--force-with-lease=refs/heads/main:<旧 SHA>` 更新 main；
   如果 main 已变化就停止并重新准备候选。不能把 rebased 分支普通合并进旧 main 来替代这一步。
7. 发布标签保持不可变，发布失败可重试；生成递增的 fork 版本号和匹配的更新日志。

## 常见问题

| 现象 | 检查 |
| --- | --- |
| 评论后完全没有运行 | 工作流是否已推送到 fork 的 main；Actions 是否启用；`BOT_ENABLED` 是否为 `true`；评论是否以 `/m7a` 开头 |
| authorize 通过但 agent 被跳过 | 查看 authorize 日志中的权限或 PR 分支限制 |
| App token 步骤报错 | Client ID、完整 PEM、App 是否已安装到这个仓库 |
| App installation is missing write permissions / 添加表情或评论返回 403 | 确认 App 的 Contents、Issues、Pull requests 均为 Read and write，再到 Settings → Applications → Installed GitHub Apps 接受权限更新；不要只修改 App 设置而未更新安装授权 |
| Cannot read AI_API_KEY | 是否为仓库级 Variable；App 是否有 Variables: read；权限变更是否已接受；也可改用 Secret |
| 模型返回 401 / 403 | API Key 与供应商接口权限 |
| 模型返回 404 | Base URL、协议和模型 ID 是否匹配 |
| 日志反复 loop、没有模型输出 | 检查 Base URL 是否遗漏 `/v1`，使 API 路径返回 200 HTML；配置检查现在会提前拦截这种情况。修复工作流后必须启动新运行，重跑旧运行仍使用旧工作流 |
| 模型只回复、不调用工具 | 模型或网关是否支持 tool calling；请求本身是否只是让它解释 |
| Git push 报作者或认证错误 | 不要移除 bot identity 步骤；此模式需要 checkout 使用 App token 并保留认证 |
| 无法提交 workflow 文件 | 此阶段 App 没有 Workflows 写权限，机器人工作范围不包含 CI 工作流 |

官方参考：

* [注册 GitHub App](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app)
* [在 Actions 中使用 App 安装令牌](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/making-authenticated-api-requests-with-a-github-app-in-a-github-actions-workflow)
* [OpenCode GitHub 集成](https://opencode.ai/docs/github/)
* [OpenCode 自定义供应商](https://opencode.ai/docs/providers/#custom-provider)
