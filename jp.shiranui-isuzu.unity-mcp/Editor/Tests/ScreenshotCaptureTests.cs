using System.Collections.Generic;

using NUnit.Framework;

using UnityMCP.Editor.Handlers;

namespace UnityMCP.Editor.Tests
{
    /// <summary>
    /// Validates the <see cref="ScreenshotCapture.ViewToTypeName"/> mapping that
    /// drives EditorWindow discovery for the /capture_screenshot endpoint.
    /// Full P/Invoke capture paths require a live Editor window and are not
    /// unit-testable here; they are exercised via integration curl tests.
    /// </summary>
    [TestFixture]
    internal sealed class ScreenshotCaptureTests
    {
        private static readonly string[] ExpectedKeys =
        {
            "inspector",
            "hierarchy",
            "project",
            "console",
            "game_view_window",
            "scene_view_window",
        };

        [Test]
        public void ViewToTypeName_HasAllExpectedKeys()
        {
            var map = ScreenshotCapture.ViewToTypeName;

            foreach (var key in ExpectedKeys)
            {
                Assert.IsTrue(map.ContainsKey(key), $"Expected key '{key}' missing from ViewToTypeName.");
            }

            Assert.AreEqual(ExpectedKeys.Length, map.Count, "ViewToTypeName has unexpected extra keys.");
        }

        [Test]
        public void ViewToTypeName_NoDuplicateTypeNames()
        {
            var map = ScreenshotCapture.ViewToTypeName;
            var seen = new HashSet<string>();

            foreach (var kv in map)
            {
                Assert.IsTrue(seen.Add(kv.Value), $"Duplicate type name mapped: '{kv.Value}' (key '{kv.Key}').");
            }
        }

        [Test]
        public void ViewToTypeName_NoEmptyKeysOrValues()
        {
            var map = ScreenshotCapture.ViewToTypeName;

            foreach (var kv in map)
            {
                Assert.IsFalse(string.IsNullOrEmpty(kv.Key), "Found empty key in ViewToTypeName.");
                Assert.IsFalse(string.IsNullOrEmpty(kv.Value), $"Found empty value for key '{kv.Key}'.");
            }
        }

        [Test]
        public void ViewToTypeName_AllValuesStartWithUnityEditor()
        {
            var map = ScreenshotCapture.ViewToTypeName;

            foreach (var kv in map)
            {
                Assert.IsTrue(
                    kv.Value.StartsWith("UnityEditor."),
                    $"Mapped type '{kv.Value}' for key '{kv.Key}' should be in the UnityEditor namespace.");
            }
        }
    }
}
