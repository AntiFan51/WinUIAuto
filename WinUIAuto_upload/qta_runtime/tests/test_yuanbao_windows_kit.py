# -*- coding: utf-8 -*-

import os
import sys
import unittest
from unittest import mock


PROJECT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SCRIPTS_ROOT = os.path.join(PROJECT_ROOT, "scripts")
if SCRIPTS_ROOT not in sys.path:
    sys.path.insert(0, SCRIPTS_ROOT)

from yuanbao_windows_kit.kit import Locator, Target, YuanbaoWindowDriver


class _Rectangle(object):
    def __init__(self, left, top, right, bottom):
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom

    def width(self):
        return self.right - self.left

    def height(self):
        return self.bottom - self.top

    def mid_point(self):
        point = mock.Mock()
        point.x = self.left + self.width() // 2
        point.y = self.top + self.height() // 2
        return point


def _wrapper(name, control_type, handle=0, class_name="", rectangle=None):
    item = mock.Mock()
    item.handle = handle
    item.process_id.return_value = 4242
    item.element_info.name = name
    item.element_info.control_type = control_type
    item.element_info.automation_id = ""
    item.element_info.class_name = class_name
    item.element_info.offscreen = False
    item.rectangle.return_value = rectangle or _Rectangle(10, 10, 110, 50)
    return item


class YuanbaoWindowDriverMultiWindowTest(unittest.TestCase):
    def setUp(self):
        self.driver = YuanbaoWindowDriver()
        self.main = _wrapper("元宝", "Window", handle=101, class_name="Tauri Window",
                             rectangle=_Rectangle(0, 0, 2000, 1200))
        self.popup = _wrapper("", "Window", handle=202, class_name="Tauri Window",
                              rectangle=_Rectangle(600, 200, 1400, 1000))
        self.driver._set_primary_window(self.main)
        self.driver._window_roots = mock.Mock(return_value=[self.main, self.popup])

    def test_find_keeps_existing_main_window_priority(self):
        main_target = _wrapper("搜索", "Edit")
        popup_target = _wrapper("搜索", "Edit")
        self.main.descendants.return_value = [main_target]
        self.popup.descendants.return_value = [popup_target]

        found = self.driver.find(Target(
            role="搜索框",
            locators=(Locator("name_and_control_type", {"name": "搜索", "control_type": "Edit"}),),
        ), timeout=0.1)

        self.assertIs(found, main_target)
        self.assertIs(self.driver.active_window, self.main)

    def test_find_falls_back_to_companion_window(self):
        self.main.descendants.return_value = [_wrapper("搜索", "Group")]
        popup_target = _wrapper("搜索", "Edit")
        self.popup.descendants.return_value = [popup_target]

        found = self.driver.find(Target(
            role="搜索框",
            locators=(Locator("name_and_control_type", {"name": "搜索", "control_type": "Edit"}),),
        ), timeout=0.1)

        self.assertIs(found, popup_target)
        self.assertIs(self.driver.active_window, self.popup)

    def test_click_focuses_the_window_that_owned_the_match(self):
        popup_target = _wrapper("搜索", "Edit")
        self.main.descendants.return_value = []
        self.popup.descendants.return_value = [popup_target]

        self.driver.click(Target(
            role="搜索框",
            locators=(Locator("name_and_control_type", {"name": "搜索", "control_type": "Edit"}),),
        ), timeout=0.1)

        self.popup.set_focus.assert_called()
        popup_target.click_input.assert_called_once_with(button="left")
        self.assertIs(self.driver.window, self.main)

    @mock.patch("yuanbao_windows_kit.kit.mouse.move")
    def test_hover_moves_to_live_uia_element_center(self, mouse_move):
        row = _wrapper("", "Group", class_name="Item_chatOrProjectItem__hash",
                       rectangle=_Rectangle(10, 20, 210, 120))
        self.main.descendants.return_value = [row]
        self.popup.descendants.return_value = []

        found = self.driver.hover(Target(
            role="first chat row",
            locators=(Locator("class_name_re", r"Item_chatOrProjectItem"),),
        ), timeout=0.1)

        self.assertIs(found, row)
        self.main.set_focus.assert_called()
        mouse_move.assert_called_once_with(coords=(110, 70))

    def test_coordinate_fallback_remains_bound_to_primary_window(self):
        self.driver.active_window = self.popup

        point = self.driver._safe_fallback_point(Target(
            role="旧单窗口目标",
            fallback_point=(1000, 600),
            reference_window_size=(2000, 1200),
        ))

        self.assertEqual(point, (1000, 600))
        self.main.set_focus.assert_called()
        self.assertIs(self.driver.window, self.main)

    def test_closed_companion_window_recovers_to_main_for_screenshot(self):
        self.driver.active_window = self.popup
        self.popup.set_focus.side_effect = RuntimeError("window closed")

        selected = self.driver._root()

        self.assertIs(selected, self.main)
        self.main.set_focus.assert_called()
        self.assertIs(self.driver.active_window, self.main)

    def test_same_process_companion_does_not_depend_on_title_or_process_probe(self):
        self.popup.element_info.name = ""
        self.driver._matches_process = mock.Mock(side_effect=AssertionError("must not probe"))

        self.assertTrue(self.driver._is_companion_window(self.popup))
        self.driver._matches_process.assert_not_called()


if __name__ == "__main__":
    unittest.main()
