"""Build/deploy regressions; no SDK or application startup needed for these cases."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import build_desktop as builder


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='mztools-build-tests-')
        self.root = Path(self.temp.name).resolve()
        self.override = patch.object(builder, 'ROOT', self.root)
        self.override.start()
        self.work = self.root / 'obj/desktop/win-x64'
        self.work.mkdir(parents=True)
        self.target = self.root / 'bin/win-x64'
        self.target.mkdir(parents=True)
        (self.target / 'MZTools.Desktop.exe').write_bytes(b'old executable')
        (self.target / 'obsolete.dll').write_bytes(b'old dependency')
        self.state = {'directory': str(self.target), 'files': {'MZTools.Desktop.exe': 'old', 'obsolete.dll': 'old'}}

    def tearDown(self):
        self.override.stop()
        self.temp.cleanup()

    def payload(self):
        path = self.work / 'payload'
        path.mkdir()
        (path / 'MZTools.Desktop.exe').write_bytes(b'new executable')
        return path

    def test_replacement_removes_obsolete_dependency_and_keeps_backup_outside_bin(self):
        builder.promote(self.payload(), self.target, self.work, self.state)
        self.assertEqual(['MZTools.Desktop.exe'], sorted(p.name for p in self.target.iterdir()))
        self.assertEqual(b'new executable', (self.target / 'MZTools.Desktop.exe').read_bytes())
        self.assertEqual(b'old executable', (self.work / 'previous/MZTools.Desktop.exe').read_bytes())

    def test_failed_promotion_rolls_back_original_files(self):
        with self.assertRaisesRegex(RuntimeError, 'previous build was preserved'):
            builder.promote(self.work / 'missing-payload', self.target, self.work, self.state)
        self.assertEqual(b'old executable', (self.target / 'MZTools.Desktop.exe').read_bytes())
        self.assertTrue((self.target / 'obsolete.dll').exists())

    def test_user_media_cannot_be_removed_by_rebuild(self):
        media = self.target / 'my-recording.wav'
        media.write_bytes(b'user recording')
        with self.assertRaisesRegex(RuntimeError, 'unexpected files'):
            builder.promote(self.payload(), self.target, self.work, self.state)
        self.assertEqual(b'user recording', media.read_bytes())
        self.assertEqual(b'old executable', (self.target / 'MZTools.Desktop.exe').read_bytes())

    def test_unmanaged_existing_directory_is_preserved(self):
        with self.assertRaisesRegex(RuntimeError, 'not owned'):
            builder.promote(self.payload(), self.target, self.work, None)
        self.assertTrue((self.target / 'obsolete.dll').exists())

    def test_cleanup_rejects_parent_and_outside_paths(self):
        for path in [self.root, self.root / 'obj/desktop', self.root / 'src', self.root.parent]:
            with self.subTest(path=path), self.assertRaises(ValueError):
                builder.remove_owned(path, self.root / 'obj/desktop')
        self.assertTrue(self.target.exists())

    def test_publish_failure_never_replaces_existing_build(self):
        with patch.object(builder, 'source_fingerprint', return_value=('changed', '10.0.303')), \
             patch.object(builder.subprocess, 'run', side_effect=RuntimeError('publish failed')):
            (self.work / 'latest.json').write_text(json.dumps(self.state), encoding='utf-8')
            with self.assertRaisesRegex(RuntimeError, 'publish failed'):
                builder.build('win-x64')
        self.assertEqual(b'old executable', (self.target / 'MZTools.Desktop.exe').read_bytes())
        self.assertEqual(self.state, json.loads((self.work / 'latest.json').read_text(encoding='utf-8')))


if __name__ == '__main__':
    unittest.main()
