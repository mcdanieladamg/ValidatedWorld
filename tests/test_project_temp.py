import tempfile
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1] / 'src'))
from validated_world.path_safety import project_temp_root, is_junction


class ProjectTempTests(unittest.TestCase):
    def test_missing_base_is_never_created(self):
        with tempfile.TemporaryDirectory() as temporary:
            missing = Path(temporary).resolve() / 'missing'
            with self.assertRaisesRegex(NotADirectoryError, str(missing).replace('\\', '\\\\')):
                project_temp_root(missing)
            self.assertFalse(missing.exists())

    def test_existing_shared_parent_is_reused_without_touching_neighbors(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            shared = root / 'tmp' / 'validated-world'
            shared.mkdir(parents=True)
            note = shared / 'human.txt'
            note.write_bytes(b'keep')
            self.assertEqual(project_temp_root(root), shared)
            self.assertEqual(note.read_bytes(), b'keep')

    def test_linked_base_or_shared_parent_is_rejected_before_allocation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            for target in (root, root / 'tmp', root / 'tmp' / 'validated-world'):
                with self.subTest(target=target), patch('validated_world.path_safety.is_junction',
                        side_effect=lambda p: p == target or is_junction(p)):
                    with self.assertRaisesRegex(ValueError, 'linked temporary workspace path'):
                        project_temp_root(root)

    def test_link_replacement_during_parent_creation_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            shared_parent = root / 'tmp'
            original = Path.mkdir

            def create_then_replace(path, *args, **kwargs):
                result = original(path, *args, **kwargs)
                if path == shared_parent:
                    linked.add(path)
                return result

            linked = set()
            with patch.object(Path, 'mkdir', create_then_replace), \
                    patch('validated_world.path_safety.is_junction', side_effect=lambda p: p in linked or is_junction(p)):
                with self.assertRaisesRegex(ValueError, 'linked temporary workspace path'):
                    project_temp_root(root)
            self.assertFalse((shared_parent / 'validated-world').exists())

