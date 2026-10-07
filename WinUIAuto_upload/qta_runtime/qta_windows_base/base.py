# -*- coding: utf-8 -*-
"""QTA lifecycle adapter for Yuanbao Windows generated cases."""

from __future__ import absolute_import

import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
_PROJECT_ROOT = os.path.dirname(_HERE)
_SCRIPTS = os.path.join(_PROJECT_ROOT, "scripts")
if _PROJECT_ROOT not in sys.path:
    sys.path.insert(0, _PROJECT_ROOT)
if _SCRIPTS not in sys.path:
    sys.path.insert(1, _SCRIPTS)

from testbase.testcase import TestCase
from yuanbao_windows_kit import YBWindowsKit


def _snake(value):
    value = re.sub(r"(.)([A-Z][a-z]+)", r"\1_\2", value)
    return re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value).lower()


class YBWindowsQtaBase(TestCase):
    """Base class generated Windows cases must inherit."""

    owner = "yuanbao-ai"
    timeout = 300
    priority = TestCase.EnumPriority.Normal
    status = TestCase.EnumStatus.Ready
    window_title_re = r".*元宝.*"
    process_name = "yuanbao.exe"

    def init_test(self, testresult):
        super(YBWindowsQtaBase, self).init_test(testresult)
        self._testresult = testresult
        self.kit = None

    def pre_test(self):
        shot_dir = os.path.join(
            os.environ.get("TEMP", os.getcwd()),
            "yb_windows_{}".format(_snake(type(self).__name__)),
        )
        self.start_step("[YBWindowsQtaBase.pre_test] 连接元宝 Windows 客户端")
        self.kit = YBWindowsKit(
            testcase=self,
            shot_dir=shot_dir,
            window_title_re=self.window_title_re,
            process_name=self.process_name,
        )
        self.kit.attach(timeout=20)

    def post_test(self):
        if self.kit is not None:
            try:
                self.kit.screenshot("final")
            except Exception:
                pass
