# Windows Duo

把 [iPhone Duo](https://github.com/sumimakito/Mac-Duo) 那种折叠透视虚化做到 Windows 主屏幕上：画面像绕底边转动的玻璃，近轴清晰，远边均匀柔和地虚化并变暗。

效果实现参考 [Mac Duo](https://github.com/sumimakito/Mac-Duo)（透视 homography、高斯金字塔、按离转轴距离变暗）。

## 运行

需要 .NET 9 Windows Desktop Runtime。

```powershell
dotnet run --project WindowsDuo.csproj -c Release
```

启动后不弹出窗口，图标在右下角通知区域（可能藏在 `^` 里）。

- 左键托盘图标：打开设置
- 右键托盘图标：开始/停止效果、设置、退出
- `Ctrl+Shift+B`：开关效果
- `Esc`：效果开启时立刻关掉（不需要设置窗口在前台）

退出请用托盘菜单「退出」。

## 效果

- 折叠角度 **0–55°**：透视拉伸 + 高斯模糊 + 远边变暗（对应原先 0–40° 的行程）
- **55° 之后**：画面不再继续拉伸，整屏逐渐变黑（到 80° 全黑）
- 转轴默认在屏幕底边，也可切到左边
- 任务栏会一起参与虚化；效果层点击可穿透到下面的窗口

## 构建

```powershell
dotnet build WindowsDuo.csproj -c Release
```

日志写在 `%TEMP%\windows-duo.log`。
