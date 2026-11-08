# Scene View Gizmo

画面角落的视角坐标轴。这个包不引用 Runtime Transform Gizmos，复制整个 `com.xiaojingspace.scene-view-gizmo` 文件夹即可单独使用。

## 安装

把本目录放到任意 Unity 工程的 `Packages` 下。Package Manager 会把它识别为嵌入式包。工程需要 Input System 和 uGUI。

## 使用

菜单 **GameObject > Scene View Gizmo**，或把组件加到相机上。

- 点击色锥，相机对齐到该轴。
- 点击中心方块或 Persp / Ortho，切换投影。
- 点击 `<`，回到上一个视角。

相机如果渲染到 RenderTexture，把显示这块纹理的 RawImage 拖到 **Display**。坐标轴画进纹理，文字和点击跟这块界面对齐。

## 示例

Package Manager 中打开 **Scene View Gizmo**，在 Samples 里导入 **UGUI 画面**。导入后的场景在 `Assets/Samples/Scene View Gizmo/1.0.0/UGUI 画面`。
