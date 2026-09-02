关于“三角洲行动”的跑刀项目开发



一 、CONDA



利用到conda，首先安装了一个我感觉稳定的python环境，conda可以独立出python环境。



conda create -n DeltaUnlimited python=3.12



会出现三个接受条款（1a，2a，3a）

process直接Y

\-----------等待安装----------



由于目前用到了vpn，所以现在暂时不考虑镜像下载的事



激活环境



conda activate DeltaUnlimited





PS：vscode快速初始化conda 环境步骤：：：：
前置：Miniconda 已装在 D:\\conda\\miniconda3，且已安装这几个扩展（你机器上都有了）：Python（ms-python.python）、Python Environments（ms-python.vscode-python-envs）、Pylance。



第 1 步：打开项目



文件 → 打开文件夹 选择 D:\\AI\\DeltaUnlimited（是打开文件夹，不是打开单个文件）。

第 2 步：选择或创建 conda 环境



已有环境（推荐）：Ctrl+Shift+P → 输入 Python: Select Interpreter → 选择 DeltaUnlimited（显示为 conda / Python 3.12.8）。之后 VSCode 会自动记住，右下角状态栏会显示当前解释器。

新建环境：Ctrl+Shift+P → Python: Create Environment → 选 Conda → 选 Python 版本（如 3.12）→ 输入环境名 → 等待完成。

提醒：新环境会出现在 D:\\conda\\miniconda3\\envs\\ 下，项目文件夹里不会多出任何东西，这代表成功而不是失败。

第 3 步：验证终端



重启 VSCode 后按 Ctrl+`` 打开新终端，依次输入：

conda --version → 显示 conda 26.5.3

conda env list → 看到 base 和 DeltaUnlimited

conda activate DeltaUnlimited → 提示符前出现 (DeltaUnlimited)

第 4 步：安装依赖



conda 包：conda install numpy pandas（慢但依赖解析稳）

pip 包：pip install requests（快；环境里 pip 26.1.2 已就绪）

建议把依赖固化：pip freeze > requirements.txt 或 conda env export > environment.yml，换机器一键重建。

第 5 步：运行与调试



打开 main.py → 右上角 ▶ Run Python File → 输出在下方终端显示

F5 断点调试；右下角显示的解释器名就是当前生效环境

第 6 步：遇到"感觉失败了"时先自查这 4 条



conda env list — 环境是否真的存在（看 envs\\ 目录，别看项目文件夹）

VSCode 右下角选中的解释器是不是目标环境

终端提示符前有没有 (环境名) 前缀

改过设置后有没有执行 Reload Window

小知识补充：项目里可能出现的是 .venv（venv 环境的产物）；conda 不会在项目里留任何文件夹。删环境用 conda remove -n DeltaUnlimited --all。

