# -*- coding: utf-8 -*-
"""Stable pywinauto execution layer for generated Yuanbao Windows QTA cases."""

from __future__ import absolute_import

import os
import re
import time
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, List, Optional, Sequence, Tuple

from pywinauto import Desktop, keyboard, mouse
from testbase import logger


@dataclass(frozen=True)
class Locator:
    """One UIA locator candidate, ordered by descending reliability."""

    by: str
    value: Any
    control_type: Optional[str] = None
    confidence: float = 1.0


@dataclass(frozen=True)
class Target:
    """A semantic target with ordered locators and an optional window-relative fallback."""

    role: str
    locators: Sequence[Locator] = field(default_factory=tuple)
    fallback_point: Optional[Tuple[int, int]] = None
    reference_window_size: Optional[Tuple[int, int]] = None


class YuanbaoWindowDriver:
    """Owns application attachment, UIA lookup, input injection, and screenshots."""

    def __init__(self, window_title_re=r".*元宝.*", process_name="yuanbao.exe"):
        self.window_title_re = window_title_re
        self.process_name = process_name
        # Yuanbao's visible Chromium/WebView window is currently hosted by
        # WeChatAppEx.exe while the application lifecycle process is
        # yuanbao.exe. The title regex remains mandatory, so unrelated WeChat
        # host windows are not attached accidentally.
        self.process_aliases = {"yuanbao.exe", "wechatappex.exe"}
        self.window = None
        self.active_window = None
        self.primary_process_id = None
        self.primary_class_name = ""

    def attach(self, timeout=20):
        deadline = time.time() + timeout
        last_error = None
        while time.time() < deadline:
            # Resolve HWNDs through Win32 first. Full desktop UIA enumeration
            # is a slow compatibility fallback on Chromium-heavy desktops.
            try:
                for handle in self._candidate_hwnds():
                    try:
                        candidate = Desktop(backend="uia").window(handle=handle).wrapper_object()
                        self._set_primary_window(candidate)
                        self.activate()
                        return candidate
                    except Exception as error:
                        last_error = error
            except Exception as error:
                last_error = error
            try:
                candidates = Desktop(backend="uia").windows(title_re=self.window_title_re, visible_only=True)
                for candidate in candidates:
                    try:
                        if not self._matches_process(candidate):
                            continue
                        self._set_primary_window(candidate)
                        self.activate()
                        return candidate
                    except Exception as error:
                        last_error = error
            except Exception as error:
                last_error = error
            time.sleep(0.4)
        raise RuntimeError("未找到元宝窗口：title_re={!r}, last_error={!r}".format(
            self.window_title_re, last_error))

    def _candidate_hwnds(self):
        """Return visible Yuanbao HWNDs without enumerating the desktop UIA tree."""
        import win32api
        import win32con
        import win32gui
        import win32process

        title_pattern = re.compile(self.window_title_re)
        expected_process = os.path.basename(self.process_name or "").lower()
        handles = []

        def collect(handle, _extra):
            if not win32gui.IsWindowVisible(handle):
                return True
            title = win32gui.GetWindowText(handle) or ""
            if not title_pattern.match(title):
                return True
            process_handle = None
            try:
                _thread_id, process_id = win32process.GetWindowThreadProcessId(handle)
                if expected_process:
                    process_handle = win32api.OpenProcess(
                        win32con.PROCESS_QUERY_INFORMATION | win32con.PROCESS_VM_READ,
                        False,
                        process_id,
                    )
                    executable = os.path.basename(win32process.GetModuleFileNameEx(process_handle, 0)).lower()
                    if executable != expected_process and executable not in self.process_aliases:
                        return True
                rectangle = win32gui.GetWindowRect(handle)
                area = max(0, rectangle[2] - rectangle[0]) * max(0, rectangle[3] - rectangle[1])
                handles.append((area, handle))
            except Exception:
                return True
            finally:
                if process_handle is not None:
                    process_handle.Close()
            return True

        win32gui.EnumWindows(collect, None)
        return [handle for _area, handle in sorted(handles, reverse=True)]

    def _set_primary_window(self, candidate):
        """Remember the stable main window without conflating it with child top-level windows."""
        self.window = candidate
        self.active_window = candidate
        try:
            self.primary_process_id = candidate.process_id()
        except Exception:
            self.primary_process_id = None
        try:
            self.primary_class_name = str(getattr(candidate.element_info, "class_name", "") or "")
        except Exception:
            self.primary_class_name = ""

    @staticmethod
    def _window_handle(candidate):
        try:
            return int(getattr(candidate, "handle", 0) or 0)
        except Exception:
            return 0

    def _visible_top_level_hwnds(self):
        """Return visible HWNDs, preferring the foreground window among companions."""
        import win32gui

        foreground = win32gui.GetForegroundWindow()
        handles = []

        def collect(handle, _extra):
            if not win32gui.IsWindowVisible(handle):
                return True
            try:
                rectangle = win32gui.GetWindowRect(handle)
                area = max(0, rectangle[2] - rectangle[0]) * max(0, rectangle[3] - rectangle[1])
            except Exception:
                area = 0
            handles.append((0 if handle == foreground else 1, -area, handle))
            return True

        win32gui.EnumWindows(collect, None)
        return [handle for _foreground_rank, _negative_area, handle in sorted(handles)]

    def _is_companion_window(self, candidate):
        """Accept only top-level windows that can be tied back to the attached Yuanbao app."""
        if candidate is None:
            return False
        try:
            if self.primary_process_id and candidate.process_id() == self.primary_process_id:
                return True
        except Exception:
            pass
        if not self._matches_process(candidate):
            return False
        try:
            candidate_class = str(getattr(candidate.element_info, "class_name", "") or "")
            if self.primary_class_name and candidate_class == self.primary_class_name:
                return True
        except Exception:
            pass
        try:
            title = str(getattr(candidate.element_info, "name", "") or "")
            return re.match(self.window_title_re, title) is not None
        except Exception:
            return False

    def _window_roots(self):
        """Yield the main window first, followed by verified Yuanbao companion windows."""
        if self.window is None:
            self.attach()
        roots = [self.window]
        seen = {self._window_handle(self.window)}
        try:
            handles = self._visible_top_level_hwnds()
        except Exception:
            handles = []
        for handle in handles:
            if int(handle or 0) in seen:
                continue
            try:
                candidate = Desktop(backend="uia").window(handle=handle).wrapper_object()
                if not self._is_companion_window(candidate):
                    continue
                seen.add(int(handle or 0))
                roots.append(candidate)
            except Exception:
                continue
        return roots

    def _matches_process(self, candidate):
        if not self.process_name:
            return True
        import win32api
        import win32con
        import win32process
        handle = None
        try:
            handle = win32api.OpenProcess(
                win32con.PROCESS_QUERY_INFORMATION | win32con.PROCESS_VM_READ,
                False,
                candidate.process_id(),
            )
            executable = os.path.basename(win32process.GetModuleFileNameEx(handle, 0))
            executable = executable.lower()
            return executable == os.path.basename(self.process_name).lower() or executable in self.process_aliases
        finally:
            if handle is not None:
                handle.Close()

    def activate(self, window=None):
        if self.window is None:
            self.attach()
        selected = window or self.active_window or self.window
        try:
            selected.set_focus()
        except Exception:
            # A companion window may close as the direct result of a successful
            # click. Continue from the stable main window instead of turning the
            # after-click screenshot into a false interaction failure.
            if selected is self.window:
                raise
            selected = self.window
            selected.set_focus()
        self.active_window = selected
        return selected

    def _root(self):
        return self.activate()

    def _primary_root(self):
        if self.window is None:
            self.attach()
        return self.activate(self.window)

    @staticmethod
    def _describe_window(root):
        try:
            info = root.element_info
            return "handle={};title={!r};class={!r}".format(
                YuanbaoWindowDriver._window_handle(root),
                str(getattr(info, "name", "") or ""),
                str(getattr(info, "class_name", "") or ""),
            )
        except Exception:
            return "handle={}".format(YuanbaoWindowDriver._window_handle(root))

    @staticmethod
    def _locator_criteria(locator):
        by = str(locator.by or "").lower()
        value = locator.value
        criteria = {}
        if by in ("automation_id", "auto_id"):
            criteria["auto_id"] = str(value)
        elif by in ("name", "title", "text"):
            criteria["title"] = str(value)
        elif by in ("name_re", "title_re", "text_re"):
            criteria["title_re"] = str(value)
        elif by == "class_name":
            criteria["class_name"] = str(value)
        elif by == "class_name_re":
            criteria["class_name_re"] = str(value)
        elif by == "control_type":
            criteria["control_type"] = str(value)
        elif by in ("name_and_control_type", "control_type_and_name"):
            if isinstance(value, dict):
                criteria["title"] = str(value.get("name") or value.get("title") or "")
                criteria["control_type"] = str(value.get("control_type") or locator.control_type or "")
            elif isinstance(value, (tuple, list)) and len(value) >= 2:
                criteria["title"] = str(value[0])
                criteria["control_type"] = str(value[1])
            else:
                criteria["title"] = str(value)
                criteria["control_type"] = str(locator.control_type or "")
        elif by == "control_type_and_class" and isinstance(value, dict):
            criteria["control_type"] = str(value.get("control_type") or "")
            criteria["class_name"] = str(value.get("class_name") or "")
        else:
            return None
        if locator.control_type and "control_type" not in criteria:
            criteria["control_type"] = locator.control_type
        return {key: nested for key, nested in criteria.items() if nested}

    @staticmethod
    def _locator_criteria_variants(locator):
        """Return exact criteria plus a stable fallback for CSS-module class names."""
        criteria = YuanbaoWindowDriver._locator_criteria(locator)
        if not criteria:
            return []
        variants = [criteria]
        class_name = criteria.get("class_name")
        if class_name:
            first_token = str(class_name).split()[0]
            stable_token = re.sub(r"_{2,3}[A-Za-z0-9_-]+$", "", first_token)
            if stable_token:
                regex_criteria = dict(criteria)
                regex_criteria.pop("class_name", None)
                regex_criteria["class_name_re"] = (
                    r"(?:^|\s){}(?:_{{2,3}}[A-Za-z0-9_-]+)?(?:\s|$)".format(
                        re.escape(stable_token)))
                variants.append(regex_criteria)
        return variants

    def find(self, target, timeout=10, required=True):
        diagnostics = []
        deadline = time.time() + max(0.1, float(timeout))
        locators = sorted(target.locators, key=lambda item: item.confidence, reverse=True)
        while time.time() < deadline:
            roots_with_wrappers = []
            for root in self._window_roots():
                try:
                    roots_with_wrappers.append((root, root.descendants()))
                except Exception as error:
                    diagnostics.append("tree_error:{}:{!r}".format(
                        self._describe_window(root), error))
                    roots_with_wrappers.append((root, []))
            for locator in locators:
                criteria_variants = self._locator_criteria_variants(locator)
                if not criteria_variants:
                    diagnostics.append("unsupported:{}".format(locator.by))
                    continue
                for criteria in criteria_variants:
                    for root, wrappers in roots_with_wrappers:
                        for wrapper in wrappers:
                            try:
                                if not self._is_visible_wrapper(wrapper):
                                    continue
                                if self._matches_wrapper(wrapper, criteria):
                                    self.active_window = root
                                    return wrapper
                            except Exception as error:
                                diagnostics.append("tree_match_error:{!r}".format(error))
                        diagnostics.append("tree_miss:{}:{}".format(
                            self._describe_window(root), criteria))
            time.sleep(0.25)
        if required:
            raise RuntimeError("定位失败 role={!r}; {}".format(target.role, "; ".join(diagnostics[-8:])))
        return None

    def fallback_scope_candidates(self, target, max_depth=10):
        """Return the UIA hit node and bounded ancestors at a recorded fallback point."""
        if target is None or target.fallback_point is None:
            return []
        try:
            x, y = self._safe_fallback_point(target)
            current = Desktop(backend="uia").from_point(x, y)
            candidates = []
            root = self._primary_root()
            root_rect = root.rectangle()
            for _depth in range(max(1, int(max_depth))):
                if current is None:
                    break
                rect = current.rectangle()
                if (rect.left < root_rect.left or rect.top < root_rect.top or
                        rect.right > root_rect.right or rect.bottom > root_rect.bottom):
                    break
                candidates.append(current)
                if getattr(current, "handle", None) == getattr(root, "handle", None):
                    break
                current = current.parent()
            return candidates
        except Exception:
            return []

    @staticmethod
    def _is_visible_wrapper(wrapper):
        """Use UIA metadata because wrapper subclasses lack is_offscreen()."""
        info = wrapper.element_info
        rect = wrapper.rectangle()
        return (not bool(getattr(info, "offscreen", False)) and
                rect.width() > 0 and rect.height() > 0)

    @staticmethod
    def _matches_wrapper(wrapper, criteria):
        """Match a realized UIA wrapper against one normalized locator."""
        info = wrapper.element_info
        values = {
            "title": getattr(info, "name", "") or "",
            "auto_id": getattr(info, "automation_id", "") or "",
            "class_name": getattr(info, "class_name", "") or "",
            "control_type": getattr(info, "control_type", "") or "",
        }
        for key, expected in criteria.items():
            if key.endswith("_re"):
                actual_key = key[:-3]
                if re.search(str(expected), str(values.get(actual_key, ""))) is None:
                    return False
            elif str(values.get(key, "")) != str(expected):
                return False
        return True

    def find_in_section(self, target, start_name, end_name, timeout=10, required=True):
        """Find a visible target inside the vertical bounds of two section markers."""
        diagnostics = []
        deadline = time.time() + timeout
        locators = sorted(target.locators, key=lambda item: item.confidence, reverse=True)
        while time.time() < deadline:
            found_scope = False
            for root in self._window_roots():
                try:
                    wrappers = root.descendants()
                except Exception as error:
                    diagnostics.append("tree_error:{}:{!r}".format(
                        self._describe_window(root), error))
                    continue
                visible = []
                for wrapper in wrappers:
                    try:
                        rect = wrapper.rectangle()
                        if not self._is_visible_wrapper(wrapper):
                            continue
                        visible.append((wrapper, rect, str(getattr(wrapper.element_info, "name", "") or "")))
                    except Exception:
                        continue
                starts = sorted((item for item in visible if item[2] == str(start_name)), key=lambda item: item[1].top)
                start = starts[0] if starts else None
                if start is None:
                    diagnostics.append("scope_start_not_found:{}:{!r}".format(
                        self._describe_window(root), start_name))
                    continue
                found_scope = True
                ends = sorted((item for item in visible if item[2] == str(end_name) and
                               item[1].top > start[1].bottom), key=lambda item: item[1].top)
                top = start[1].bottom - 2
                bottom = ends[0][1].top if ends else float("inf")
                for locator in locators:
                    for criteria in self._locator_criteria_variants(locator):
                        for wrapper, rect, _name in visible:
                            try:
                                if rect.top >= top and rect.top < bottom and self._matches_wrapper(wrapper, criteria):
                                    self.active_window = root
                                    return wrapper
                            except Exception as error:
                                diagnostics.append("match_error:{!r}".format(error))
                diagnostics.append("section={}:{}->{!r};top={};bottom={}".format(
                    self._describe_window(root), start_name, end_name, top, bottom))
            if not found_scope:
                diagnostics.append("scope_not_found_in_any_yuanbao_window")
            if time.time() >= deadline:
                break
            time.sleep(0.25)
        if required:
            raise RuntimeError("范围定位失败 role={!r}; {}".format(target.role, "; ".join(diagnostics[-8:])))
        return None

    def _safe_fallback_point(self, target):
        if not target.fallback_point:
            raise RuntimeError("目标 {!r} 没有可用定位和坐标兜底".format(target.role))
        root = self._primary_root()
        rect = root.rectangle()
        x, y = (int(target.fallback_point[0]), int(target.fallback_point[1]))
        if target.reference_window_size:
            reference_width, reference_height = target.reference_window_size
            if int(reference_width) <= 0 or int(reference_height) <= 0:
                raise RuntimeError("目标 {!r} 的录制窗口尺寸无效".format(target.role))
            x = round(x * rect.width() / int(reference_width))
            y = round(y * rect.height() / int(reference_height))
        if x < 0 or y < 0 or x >= rect.width() or y >= rect.height():
            raise RuntimeError("坐标兜底超出元宝窗口 role={!r}, point={!r}, size=({}, {})".format(
                target.role, target.fallback_point, rect.width(), rect.height()))
        return rect.left + x, rect.top + y

    def click(self, target, timeout=10, button="left"):
        wrapper = self.find(target, timeout=timeout, required=True)
        self.activate(self.active_window)
        wrapper.click_input(button=button)

    def hover(self, target, timeout=10):
        wrapper = self.find(target, timeout=timeout, required=True)
        self.activate(self.active_window)
        rect = wrapper.rectangle()
        mouse.move(coords=(rect.mid_point().x, rect.mid_point().y))
        return wrapper

    def input_text(self, target, text, timeout=10, clear=True):
        wrapper = self.find(target, timeout=timeout)
        self.activate()
        try:
            wrapper.set_edit_text(str(text))
            return
        except Exception:
            wrapper.click_input()
        if clear:
            keyboard.send_keys("^a")
        self._paste_unicode(str(text))

    @staticmethod
    def _paste_unicode(text):
        import win32clipboard
        previous = None
        try:
            win32clipboard.OpenClipboard()
            try:
                previous = win32clipboard.GetClipboardData(win32clipboard.CF_UNICODETEXT)
            except Exception:
                previous = None
            win32clipboard.EmptyClipboard()
            win32clipboard.SetClipboardText(text, win32clipboard.CF_UNICODETEXT)
            win32clipboard.CloseClipboard()
            keyboard.send_keys("^v")
        finally:
            try:
                win32clipboard.OpenClipboard()
                win32clipboard.EmptyClipboard()
                if previous is not None:
                    win32clipboard.SetClipboardText(previous, win32clipboard.CF_UNICODETEXT)
                win32clipboard.CloseClipboard()
            except Exception:
                pass

    def press(self, keys):
        self.activate()
        value = str(keys).strip()
        named_keys = {
            "ENTER": "{ENTER}",
            "RETURN": "{ENTER}",
            "TAB": "{TAB}",
            "ESC": "{ESC}",
            "ESCAPE": "{ESC}",
            "BACKSPACE": "{BACKSPACE}",
            "DELETE": "{DELETE}",
            "SPACE": "{SPACE}",
            "UP": "{UP}",
            "DOWN": "{DOWN}",
            "LEFT": "{LEFT}",
            "RIGHT": "{RIGHT}",
        }
        keyboard.send_keys(named_keys.get(value.upper(), value))

    def scroll(self, wheel_delta, target=None):
        if target is not None:
            wrapper = self.find(target, required=False)
            if wrapper is not None:
                rect = wrapper.rectangle()
                mouse.move(coords=(rect.mid_point().x, rect.mid_point().y))
        self.activate()
        mouse.scroll(wheel_dist=int(wheel_delta / 120))

    def drag(self, start, end, duration_ms=500):
        root = self._primary_root()
        rect = root.rectangle()
        sx, sy = rect.left + int(start[0]), rect.top + int(start[1])
        ex, ey = rect.left + int(end[0]), rect.top + int(end[1])
        for x, y in ((sx, sy), (ex, ey)):
            if not rect.left <= x < rect.right or not rect.top <= y < rect.bottom:
                raise RuntimeError("拖拽坐标超出元宝窗口")
        mouse.move(coords=(sx, sy))
        mouse.press(button="left")
        mouse.move(coords=(ex, ey), duration=max(0.05, duration_ms / 1000.0))
        mouse.release(button="left")

    def screenshot(self, file_path):
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        self._root().capture_as_image().save(file_path)
        return file_path


class YBWindowsKit:
    """Only public behavior that AI-generated cases are allowed to call."""

    def __init__(self, testcase, shot_dir, window_title_re=r".*元宝.*", process_name="yuanbao.exe"):
        self.testcase = testcase
        self.shot_dir = shot_dir
        self.driver = YuanbaoWindowDriver(window_title_re=window_title_re, process_name=process_name)
        self._shot_index = 0

    def attach(self, timeout=20):
        return self.driver.attach(timeout=timeout)

    def step(self, label):
        self.testcase.start_step(str(label))

    def screenshot(self, label):
        self._shot_index += 1
        safe = re.sub(r"[^0-9A-Za-z._-]+", "_", str(label)).strip("_") or "shot"
        return self.driver.screenshot(os.path.join(self.shot_dir, "{:02d}_{}.png".format(self._shot_index, safe)))

    def click(self, target, timeout=10):
        self.screenshot("before_click_{}".format(target.role))
        self.driver.click(target, timeout=timeout)
        self.screenshot("after_click_{}".format(target.role))

    def right_click(self, target, timeout=10):
        self.screenshot("before_right_click_{}".format(target.role))
        self.driver.click(target, timeout=timeout, button="right")
        self.screenshot("after_right_click_{}".format(target.role))

    def click_after_hover(self, target, hover_target, timeout=10):
        self.screenshot("before_hover_{}".format(hover_target.role))
        self.driver.hover(hover_target, timeout=timeout)
        self.screenshot("after_hover_{}".format(hover_target.role))
        self.driver.click(target, timeout=timeout)
        self.screenshot("after_click_{}".format(target.role))

    def input_text(self, target, text, timeout=10, clear=True):
        self.screenshot("before_input_{}".format(target.role))
        self.driver.input_text(target, text, timeout=timeout, clear=clear)
        self.screenshot("after_input_{}".format(target.role))

    def press(self, keys):
        self.driver.press(keys)

    def scroll(self, wheel_delta=-360, target=None):
        self.driver.scroll(wheel_delta, target=target)

    def drag(self, start, end, duration_ms=500):
        self.driver.drag(start, end, duration_ms=duration_ms)

    def wait_seconds(self, seconds):
        time.sleep(max(0.0, float(seconds)))

    def wait_exists(self, target, timeout=10):
        return self.driver.find(target, timeout=timeout, required=True)

    def exists(self, target, timeout=0):
        """Return whether a target exists without failing the QTA case."""
        return self.driver.find(target, timeout=max(0.1, float(timeout)), required=False) is not None

    def exists_in_section(self, target, start_name, end_name, timeout=0):
        """Return whether target exists between named section markers."""
        return self.driver.find_in_section(
            target,
            start_name=start_name,
            end_name=end_name,
            timeout=max(0.1, float(timeout)),
            required=False,
        ) is not None

    def wait_not_exists(self, target, timeout=10):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.driver.find(target, timeout=0.4, required=False) is None:
                return True
            time.sleep(0.25)
        self._fail("等待控件消失失败", target=target, expected="不存在", actual="仍然存在")

    def _fail(self, label, target=None, expected=None, actual=None):
        try:
            shot = self.screenshot("FAIL_{}".format(label))
        except Exception as error:
            shot = "截图失败：{!r}".format(error)
        message = "{}; role={!r}; expected={!r}; actual={!r}; evidence={}".format(
            label, getattr(target, "role", None), expected, actual, shot)
        logger.error("断言：{}\n预期：{}\n实际：{}\n断言结果：失败\n证据：{}".format(
            label, expected, actual, shot))
        self.testcase.fail(message)
        raise AssertionError(message)

    def _assertion_pass(self, label, expected, actual):
        """Record a QTA-visible result without changing assertion semantics."""
        logger.info("断言：{}\n预期：{}\n实际：{}\n断言结果：通过".format(
            label, expected, actual))

    def expect_exists(self, target, timeout=10, label=None):
        wrapper = self.driver.find(target, timeout=timeout, required=False)
        if wrapper is None:
            self._fail(label or "控件应出现", target=target, expected="存在", actual="不存在")
        self._assertion_pass(label or "控件应出现", "存在", "存在")
        return wrapper

    def expect_not_exists(self, target, timeout=10, label=None):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if self.driver.find(target, timeout=0.4, required=False) is None:
                self._assertion_pass(label or "控件应消失", "不存在", "不存在")
                return True
            time.sleep(0.25)
        self._fail(label or "控件应消失", target=target, expected="不存在", actual="仍存在")

    def expect_text_contains(self, target, expected, timeout=10, label=None):
        deadline = time.time() + max(0.1, float(timeout))
        last_actual = ""
        while time.time() < deadline:
            remaining = max(0.1, deadline - time.time())
            wrapper = self.driver.find(target, timeout=min(0.5, remaining), required=False)
            candidates = [wrapper] if wrapper is not None else []
            for candidate in candidates:
                texts = [candidate.window_text()]
                try:
                    texts.extend(child.window_text() for child in candidate.descendants())
                except Exception:
                    pass
                last_actual = "\n".join(str(item) for item in texts if item)
                if str(expected) in last_actual:
                    self._assertion_pass(label or "文本应包含预期内容", expected, last_actual)
                    return last_actual
            time.sleep(0.1)
        self._fail(label or "文本应包含预期内容", target=target, expected=expected, actual=last_actual)
        return last_actual

    def expect_text_equals(self, target, expected, timeout=10, label=None):
        deadline = time.time() + max(0.1, float(timeout))
        wanted = str(expected).strip()
        last_actual = ""
        while time.time() < deadline:
            remaining = max(0.1, deadline - time.time())
            wrapper = self.driver.find(target, timeout=min(0.5, remaining), required=False)
            candidates = [wrapper] if wrapper is not None else []
            for candidate in candidates:
                texts = [candidate.window_text()]
                try:
                    texts.extend(child.window_text() for child in candidate.descendants())
                except Exception:
                    pass
                normalized = [str(item).strip() for item in texts if str(item).strip()]
                last_actual = "\n".join(normalized)
                if wanted in normalized:
                    self._assertion_pass(label or "文本应等于预期内容", expected, wanted)
                    return wanted
            time.sleep(0.1)
        self._fail(label or "文本应等于预期内容", target=target, expected=expected, actual=last_actual)
        return last_actual

    def expect_property_equals(self, target, property_name, expected, timeout=10, label=None):
        wrapper = self.expect_exists(target, timeout=timeout, label=label)
        info = wrapper.element_info
        aliases = {
            "name": "name", "automation_id": "automation_id", "class_name": "class_name",
            "control_type": "control_type", "enabled": "enabled",
        }
        if property_name == "value":
            try:
                actual = wrapper.iface_value.CurrentValue
            except Exception:
                try:
                    actual = wrapper.get_value()
                except Exception:
                    actual = "<UIA Value不可用>"
            if actual != expected:
                self._fail(label or "输入内容应等于预期值", target=target, expected=expected, actual=actual)
            self._assertion_pass(label or "输入内容应等于预期值", expected, actual)
            return actual
        attribute = aliases.get(property_name)
        if not attribute:
            self._fail(label or "不支持的属性断言", target=target, expected=property_name, actual="unsupported")
        actual = getattr(info, attribute, None)
        if actual != expected:
            self._fail(label or "属性应等于预期值", target=target, expected=expected, actual=actual)
        self._assertion_pass(label or "属性应等于预期值", expected, actual)
        return actual

    def expect_keywords(self, target, keywords, minimum=1, timeout=10, label=None):
        keyword_list = [str(word) for word in keywords if str(word)]
        required = int(minimum)
        if not keyword_list or required <= 0 or required > len(keyword_list):
            self._fail(label or "关键词断言配置无效", target=target,
                       expected="1 <= minimum <= 关键词数量",
                       actual={"minimum": required, "keywords": keyword_list})

        deadline = time.time() + max(0.1, float(timeout))
        last_hits = []
        target_seen = False
        while time.time() < deadline:
            wrapper = (self.driver._root() if target is None else
                       self.driver.find(target, timeout=min(0.8, max(0.1, deadline - time.time())), required=False))
            candidates = [wrapper] if wrapper is not None else []
            for wrapper in candidates:
                target_seen = True
                texts = [wrapper.window_text()]
                try:
                    texts.extend(child.window_text() for child in wrapper.descendants())
                except Exception:
                    pass
                haystack = "\n".join(str(item) for item in texts if item)
                last_hits = [word for word in keyword_list if word in haystack]
                if len(last_hits) >= required:
                    self._assertion_pass(label or "关键词命中数应满足要求",
                                         "至少{}个：{}".format(required, keyword_list), last_hits)
                    return last_hits
            time.sleep(0.25)

        self._fail(label or "关键词命中数不足", target=target,
                   expected="至少{}个：{}".format(required, keyword_list),
                   actual={"target_seen": target_seen, "hits": last_hits})
