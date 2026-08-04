"""Native AppKit entry point for Glitch FX."""
from __future__ import annotations

import sys
import warnings

import objc
from Cocoa import NSApplication

from ui.app_delegate import AppDelegate


warnings.filterwarnings("ignore", category=objc.ObjCPointerWarning)


def main():
    app = NSApplication.sharedApplication()
    delegate = AppDelegate.alloc().init()
    app.setDelegate_(delegate)
    app.setActivationPolicy_(0)  # NSApplicationActivationPolicyRegular
    app.activateIgnoringOtherApps_(True)
    app.run()


if __name__ == "__main__":
    main()
