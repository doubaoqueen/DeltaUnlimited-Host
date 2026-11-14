---
title: 坑-MuMu虚拟显卡残留致游戏启动黑屏
tags: [踩坑, 环境]
status: 已解决
created: 2026-11-07
related: ["[[01 项目总览]]", "[[常见问题FAQ]]"]
---

# 坑：MuMu 虚拟显卡残留致游戏启动黑屏

> [!bug] 症状
> 每次启动《三角洲行动》，画面刚要进主界面时**显示器整个黑屏**（几秒后恢复或卡更久）。反复发生（11-04、11-07 两次有日志实锤），重装游戏无效，一度被当成玄学。

## 根因

三层叠加，缺一不炸：

1. 游戏启动切全屏 → Windows **强制重新枚举显示模式**（事件 `Display 4107`，`SDC_FORCE_MODE_ENUMERATION`）——这是切全屏的正常动作，每次都发生；
2. 系统里挂着一块 **MuMu 模拟器的虚拟显示适配器**（`ROOT\DISPLAY\0000`，UMDF 驱动框架 `WUDFRd`）——重枚举时它也被牵连重新初始化，且**每次都加载失败**（事件 `Kernel-PnP 219`，状态 `0xC0000365`）；
3. 显示栈在这下震荡里重置输出口 → 真显示器丢信号 → 黑屏。机器没崩（无 TDR 4101、无重启记录）。

**为什么会有残留**：MuMu 是被**手动删文件夹**卸掉的（`D:\Recreation\MuMuPlayer` 已不存在，但注册表卸载入口还挂着）——卸载程序从未运行。而 MuMu 的卸载器本来也不会带走虚拟显卡（独立注册的驱动包），双重漏网。残留共六处：

| # | 残留 | 位置 |
|---|---|---|
| 1 | 虚拟显卡设备 | 设备树 `ROOT\DISPLAY\0000`（状态平时 OK，重枚举时炸） |
| 2 | 驱动包 | 驱动仓库 `oem0.inf`（MuMuIddDriver.inf，Display 类，UMDF） |
| 3 | 内核驱动服务 ×2 | `MuMuNxSup`、`MuMuVMMDrv`（**System 启动**，每次开机从残留文件夹加载 .sys） |
| 4 | 用户服务 | `MuMuRemoteService` |
| 5 | 文件夹 ×4 | `Program Files\MuMuNxVbox`、`MuMuVMMVbox`、`ProgramData\Netease`、`AppData\Local\NemuServer` |
| 6 | 悬空卸载入口 | 注册表 `HKLM\...\Uninstall\MuMuPlayer` 指向不存在的 uninstall.exe |

## 修复方式（2026-11-07 已执行，全部成功）

```powershell
# 0) 先核实驱动包身份（oem0.inf 不一定是它，删错驱动包很尴尬）
Get-WindowsDriver -Online | Where-Object ProviderName -eq 'MuMu'
#   → oem0.inf / mumuidddriver.inf / Display 类，确认

# 1) 摘设备、删驱动包（热操作，无需重启）
pnputil /remove-device "ROOT\DISPLAY\0000"
pnputil /delete-driver oem0.inf /uninstall /force

# 2) 内核驱动先停再删（不停则 .sys 被锁，文件夹删不掉）
sc.exe stop MuMuNxSup;  sc.exe delete MuMuNxSup
sc.exe stop MuMuVMMDrv; sc.exe delete MuMuVMMDrv
sc.exe delete MuMuRemoteService

# 3) 删残留文件夹（删前检查过 ProgramData\Netease 只有 MuMu 内容）
#    + 删悬空注册表键 HKLM\SOFTWARE\...\Uninstall\MuMuPlayer
```

验收（四项全空才算完）：显示适配器、内核驱动、服务、驱动仓库均无 MuMu。

> [!warning] 工具坑上坑：Git Bash 吃掉 pnputil 的参数
> 在 Git Bash 里直接跑 `pnputil /remove-device ...`，`/remove-device` 会被 MSYS 路径转换吃掉，pnputil 收到乱码参数后**默默打印帮助菜单**——看起来像"命令不存在"，其实命令没送达。`pnputil`/`sc` 一律套 `powershell -Command` 跑。另外 `pnputil /enum-drivers | grep -i mumu` 在 Git Bash 下返回过不可靠的空结果，驱动仓库枚举要用 `Get-WindowsDriver -Online`。

## 教训（以后怎么避免）

1. **卸载软件永远用卸载程序**，删文件夹只是删"家具"；驱动、服务、注册表是"户口"，都不在应用自己的文件夹里；
2. **模拟器/虚拟化类软件重点盯防**（MuMu/雷电/夜神/VirtualBox/VMware/Docker）：它们会装虚拟设备驱动，卸载器自己也经常漏；
3. 卸载后花一分钟验收：设备管理器（含隐藏设备的虚拟适配器）、`Get-Service`、`Get-WindowsDriver -Online`、`ProgramData`/`AppData\Local` 残留文件夹、**`HKCU/HKLM Run` 启动键**（2026-11-14 补刀：`MuMuNxMain`/`MuMuPlayerService` 两条遗民键在首轮清剿漏网两周，指向已删除文件）；
4. 排查"游戏启动黑屏"先看两个事件：`System` 日志里 `Display 4107` 与 `Kernel-PnP 219` **时间贴近** = 有虚拟显示设备在切模式时捣乱；有 4101/TDR 才是显卡驱动崩；
5. 日常缓解：游戏用"无边框窗口"减少独占全屏的模式切换（对 DeltaUnlimited 还顺带满足窗口化要求）。

## 出处

- 事件日志实锤：2026-11-07 17:12（219 + 4107 相隔 13 秒）、2026-11-04 12:28-12:33（同模式复发）、10-26 19:17（仅 4107 对照组）
- 本日清理记录：设备/驱动包/3 服务/4 文件夹/注册表键全清，四项验收为空
- 关联：[[常见问题FAQ]] 第 16 条、blog《04-MuMu虚拟显卡黑屏排查与防范手册》
