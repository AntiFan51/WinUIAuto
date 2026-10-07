# -*- coding: utf-8 -*-
"""项目 QTAF 配置文件

QTA 框架配置加载优先级（从低到高）：
1. qtaf_settings (QTA 默认配置)
2. installed_apps 的 settings
3. 本文件 (项目配置，优先级最高)
"""

import os

# -----------------------------------
# 项目信息
# -----------------------------------
PROJECT_NAME = "qta_runtime"
PROJECT_MODE = "standalone"

# -----------------------------------
# 框架行为配置
# -----------------------------------
# pre_test 失败时是否跳过 run_test
# True: pre_test 抛异常后直接标记用例失败，不再执行 run_test
# False: pre_test 失败后继续尝试执行 run_test
QTAF_FAILED_SKIP_RUNTEST = True

# -----------------------------------
# 调试配置
# -----------------------------------
DEBUG = False

# -----------------------------------
# 数据驱动
# -----------------------------------
DATA_DRIVE = False

# -----------------------------------
# 断言重写
# -----------------------------------
QTAF_REWRITE_ASSERT = False

# -----------------------------------
# 从环境变量覆盖配置（可选）
# -----------------------------------
if os.environ.get("QTAF_FAILED_SKIP_RUNTEST") == "0":
    QTAF_FAILED_SKIP_RUNTEST = False
