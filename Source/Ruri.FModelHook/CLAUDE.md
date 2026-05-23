查看 md 后继续强化 UE 的符号探索，可以并行分析源码。

要用烟雾测试 shader 反编译。同 shader 不同变体通常新增信息很少，随机抽样几个解包分析即可，不要浪费时间全部反编译。

本地路径不要写死绝对路径，使用这些环境变量：

- `ONI_VALLEY_ROOT`
- `ONI_VALLEY_FMODEL_CONFIG`
- `UE_5_1_ROOT`
- `INFINITY_NIKKI_ROOT`
- `INFINITY_NIKKI_FMODEL_CONFIG`
- `UE_5_4_ROOT`

自测试循环使用仓库相对路径：

- `Source/Ruri.FModelHook`
- `Source/Ruri.FModelHook.CLI`
- `Source/Ruri.ShaderDecompiler`

直接先反编译，然后查看有哪些 cb 符号仍然不足。如果编译后根本不可能还原，就实现一套外部 cb 符号定义；不要硬编码在代码里，而是让程序直接读取特定文件夹中特定命名规范的 metadata，例如 `[cbname]_MetaData.json`。必须在穷举所有源码真理且确认没有其它线索后才允许这样做。之后系统化整理 UE 符号来源 md，统一为 1 个极简文档，并写明所有符号的来源源码。
