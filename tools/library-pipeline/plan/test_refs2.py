"""Tiny inventories and source trees; no drive access, network or GPU.

Run: python -B -m unittest discover -s tools/library-pipeline/plan -p test_refs2.py -v
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PureWindowsPath
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import refs2_source

HERE = Path(__file__).resolve().parent
UPSCALE = HERE.parent / 'upscale'
sys.path.insert(0, str(UPSCALE))
import run_upscale2 as pipeline
import small_pc_runner as small_runner


def dump(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False), encoding='utf-8')


def classification(key, use='object', sub='effects', section='magic'):
    return dict(key=key, ru='Название', ruOne='Вариант', use=use, sub=sub, section=section,
                places=['forest'], setting=['fantasy'], state=[], season='any', search=['поиск'],
                detail='Описание', view='top')


class Refs2Tests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.lib = self.base / 'library'
        self.cls = self.base / 'cls'
        self.src = self.base / 'Pictures'
        self.inv = self.base / 'inventory.json'
        self.inv2 = self.base / 'inventory2.json'
        self.meta = self.base / 'files.json'
        self.names = self.base / 'names.json'
        self.surface = '_Магазин — поверхности (превью)\\01 Pack\\assets\\Light 1.png'
        self.effect = '_Магазин — эффекты (превью)\\01 Pack\\assets\\Light 1.png'
        self.texture = '_Текстуры (кисти)\\Fantasy World\\New_12.png'
        self.shared = '_Текстуры (кисти)\\Fantasy World\\Shared_13.png'
        self.other_sha = '_Текстуры (кисти)\\Fantasy World\\Same SHA_14.png'
        self.pixel = '_Текстуры (кисти)\\Fantasy World\\Pixel_15.png'
        self.copy = self.surface.replace('Light 1', 'Light 2')
        self.cover = '_Магазин — поверхности (превью)\\01 Pack\\_cover.png'
        self.refs_object = 'Fantasy World\\Icons\\Objects\\One_1.png'
        rows = [[self.refs_object, 1, 100, 100, 'RGBA', True],
                [self.shared, 1, 512, 512, 'RGB', False]]
        dump(self.inv, rows)
        dump(self.cls / 'chunks_out/one.json', [classification('OBJ:Objects'),
             classification('TEX:Shared', 'ground', 'ground-tex', 'textures')])
        dump(self.names, [classification('SHOP:Light', 'overlay'),
             classification('TEX:New', 'ground', 'ground-tex', 'textures'),
             classification('TEX:Same SHA', 'ground', 'ground-tex', 'textures'),
             classification('TEX:Pixel', 'ground', 'ground-tex', 'textures')])
        # Real directory layout, but metadata tests need no synthetic pictures.
        for rel in (self.refs_object, self.shared):
            target = self.src.joinpath('refs', *PureWindowsPath(rel).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b'refs')
        rows2, metadata = [], []
        for rel in (self.surface, self.effect, self.texture, self.shared, self.other_sha, self.pixel, self.copy, self.cover):
            data = b'same' if rel in (self.surface, self.copy) else rel.encode()
            target = self.src.joinpath('refs2', *PureWindowsPath(rel).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            rows2.append([rel, len(data), 160, 100, 'RGBA', True])
            metadata.append(dict(rel=rel, sha256=hashlib.sha256(data).hexdigest(),
                kind='shop-cover' if rel == self.cover else 'texture' if rel.startswith('_Текстуры') else 'shop-asset',
                inRefsBySha=['refs-copy'] if rel == self.other_sha else [],
                relInRefs=rel == self.shared, dupKeep=self.surface if rel == self.copy else rel))
        dump(self.inv2, rows2)
        dump(self.meta, metadata)
        self.reviewed = self.base / 'pixel-duplicates.json'
        dump(self.reviewed, [self.pixel])
        # Exercise existing post-split fixes and their exact applied.json output.
        dump(self.lib / 'plan/fixes/name_fixes.json', [dict(key='fantasy-world|OBJ:Objects',
             field='name', old='Название', new='Исправлено')])
        self.lib.joinpath('upscaled').mkdir()
        self.manifest = self.lib / 'upscaled/manifest.jsonl'
        self.manifest.write_text(json.dumps(dict(rel='refs2\\' + self.surface, ok=True, factor=4)) + '\n')

    def options(self):
        return ['--refs2-inventory', str(self.inv2), '--refs2-files', str(self.meta),
                '--refs2-classification', str(self.names), '--refs2-root', str(self.src),
                '--refs2-pixel-duplicates', str(self.reviewed), '--check-refs2-sources']

    def run_script(self, script, *args):
        result = subprocess.run([sys.executable, '-B', str(script), *map(str, args)],
            env=os.environ | {'MAP_LIBRARY_ROOT': str(self.lib)}, text=True, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def plan(self, *extra):
        self.run_script(HERE / 'plan.py', self.cls, self.inv, *extra)
        return json.loads((self.lib / 'plan/sets.json').read_bytes())

    def test_refs_bytes_and_fixes_unchanged(self):
        self.plan()
        baseline = (self.lib / 'plan/sets.json').read_bytes()
        # Frozen output was produced by plan.py from the checked-out HEAD, before this patch.
        self.assertEqual(hashlib.sha256(baseline.replace(b'\r\n', b'\n')).hexdigest(),
                         '3d0bb18564cb1b062907b839574258cc818b6966ea90951b325fdcd8c5a85e36')
        fixed = (self.lib / 'plan/fixes/applied.json').read_bytes()
        unclassified = (self.lib / 'plan/unclassified.json').read_bytes()
        combined = self.plan(*self.options())
        refs = [s for s in combined if s.get('source') != 'refs2']
        self.assertEqual(json.dumps(refs, ensure_ascii=False, indent=1).replace('\n', os.linesep).encode(), baseline)
        self.assertEqual((self.lib / 'plan/fixes/applied.json').read_bytes(), fixed)
        self.assertEqual((self.lib / 'plan/unclassified.json').read_bytes(), unclassified)

    def test_new_catalogs_overlay_originals_readiness_and_prefix(self):
        combined = self.plan(*self.options())
        extra = [s for s in combined if s.get('source') == 'refs2']
        self.assertEqual({s['catalog'] for s in extra}, {'shop-surfaces', 'shop-effects', 'textures-2'})
        self.assertTrue(all(f.startswith('refs2\\') for s in extra for f in s['files']))
        shop = {s['catalog']: s for s in extra if s['catalog'].startswith('shop-')}
        self.assertTrue(all(s['use'] == 'object' for s in shop.values()))
        self.assertEqual(shop['shop-surfaces']['collection'], ['Тестовое', 'Магия и эффекты', 'Эффекты'])
        self.assertEqual(shop['shop-surfaces']['detail'], 'Описание')
        self.assertIn('Вид/Сверху', shop['shop-surfaces']['tags'])
        self.assertTrue(shop['shop-surfaces']['ready'])
        self.assertFalse(shop['shop-effects']['ready'])
        textures = [s for s in extra if s['catalog'] == 'textures-2']
        self.assertTrue(all(s['ready'] and s['factor'] == [] for s in textures))
        # Same path with different bytes is retained unless explicitly reviewed as equivalent.
        self.assertIn('refs2\\' + self.shared, [f for s in textures for f in s['files']])
        kept = {f for s in extra for f in s['files']}
        for dropped in (self.copy, self.cover, self.other_sha, self.pixel):
            self.assertNotIn('refs2\\' + dropped, kept)

    def test_lists_keep_all_and_trial_bytes_and_match_shop_plan(self):
        self.run_script(HERE / 'make_lists.py', self.inv)
        before = [(self.lib / 'lists' / name).read_bytes() for name in ('all.txt', 'trial.txt')]
        options = self.options()
        index = options.index('--refs2-classification')
        del options[index:index + 2]
        self.run_script(HERE / 'make_lists.py', self.inv, *options)
        self.assertEqual(before, [(self.lib / 'lists' / name).read_bytes() for name in ('all.txt', 'trial.txt')])
        self.assertEqual((self.lib / 'lists/refs2.txt').read_text(encoding='utf-8').splitlines(),
                         ['refs2\\' + self.surface, 'refs2\\' + self.effect])

    def test_sha_against_refs_manifest_and_internal_duplicate_without_hint(self):
        args = small_runner.argparse.Namespace(refs2_inventory=self.inv2, refs2_files=self.meta,
            refs2_pixel_duplicates=None, refs2_root=self.src, refs2_prefix='refs2', check_refs2_sources=False)
        files = json.loads(self.meta.read_bytes())
        next(f for f in files if f['rel'] == self.copy).pop('dupKeep')
        dump(self.meta, files)
        sha = next(f['sha256'] for f in files if f['rel'] == self.texture)
        rows, stats = refs2_source.selected(args, [sha])
        self.assertNotIn(self.texture, [r[0] for r in rows])
        self.assertEqual(stats['duplicate_refs_sha'], 2)
        self.assertEqual(stats['duplicate_refs2'], 1)

    def test_refs2_shop_goes_direct_to_cycle_and_refs_keeps_old_call(self):
        # Mock only image I/O and network outputs; verify the actual PC dispatch before verify().
        from PIL import Image
        image = Image.open(HERE.parents[2] / 'tests/fixtures/assets/owner-mountain-01.png').convert('RGBA')
        small = image.resize((128, 128))
        for rel, expected in [('refs2\\' + self.surface, True), ('_Магазин\\a.png', False)]:
            source = self.base / rel
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(b'owner-source-hash-only')
            calls = []
            def finish(*args, **kwargs):
                calls.append(kwargs)
                Path(args[3]).write_bytes(b'mocked-output')
                return (512, 512)
            with patch.object(pipeline.recipe, 'load_rgba', return_value=small), \
                    patch.object(pipeline.recipe, 'finish', side_effect=finish), \
                    patch.object(pipeline.recipe, 'verify', return_value=(True, 'ok', 0.0, 0.0)):
                row = pipeline.finish_one(self.base, rel, 0, self.base, pipeline.Config(self.lib))
            self.assertTrue(row['ok'], row)
            self.assertEqual(calls[0].get('cycle_rgb', False), expected)
            self.assertEqual(row.get('recipe'), 'cycle_rgb' if expected else None)
            self.assertEqual(row['factor'], 4)

    def test_unsafe_prefix_rejected(self):
        with self.assertRaises(ValueError):
            refs2_source.safe_rel('..\\refs2')

    def test_cycle_rgb_math_keeps_alpha_and_improves_colour(self):
        import numpy as np
        from PIL import Image
        with Image.open(HERE.parents[2] / 'tests/fixtures/assets/owner-mountain-01.png') as original:
            src = original.convert('RGBA').resize((64, 64))
        model = np.asarray(src.resize((256, 256)).convert('RGB'), np.int16)
        model = Image.fromarray(np.clip(model + 25, 0, 255).astype(np.uint8))
        a = self.base / 'model.png'
        model.save(a)
        normal, cycle = self.base / 'normal.webp', self.base / 'cycle.webp'
        pipeline.recipe.finish(src, a, a, normal, 4)
        pipeline.recipe.finish(src, a, a, cycle, 4, cycle_rgb=True)
        with Image.open(normal) as x, Image.open(cycle) as y:
            self.assertEqual(y.size, (256, 256))
            np.testing.assert_array_equal(np.asarray(x.convert('RGBA'))[..., 3], np.asarray(y.convert('RGBA'))[..., 3])
        old = pipeline.recipe.verify(src, normal, 4)
        new = pipeline.recipe.verify(src, cycle, 4)
        self.assertTrue(new[0], new)
        self.assertLess(new[3], old[3])

    def test_mac_worker_dispatches_cycle_before_verification(self):
        spec = importlib.util.spec_from_file_location('test_mac_worker', HERE.parent / 'mac/mac_worker.py')
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)
        from PIL import Image
        with Image.open(HERE.parents[2] / 'tests/fixtures/assets/owner-mountain-01.png') as original:
            small = original.convert('RGBA').resize((128, 128))
        job = self.base / 'job'
        (job / 'src').mkdir(parents=True)
        (job / 'out').mkdir()
        (job / 'src/0.png').write_bytes(b'owner-source-hash-only')
        calls = []
        def finish(*args, **kwargs):
            calls.append(kwargs)
            Path(args[3]).write_bytes(b'mocked-output')
            return (512, 512)
        with patch.object(worker.recipe, 'load_rgba', return_value=small), \
                patch.object(worker.recipe, 'finish', side_effect=finish), \
                patch.object(worker.recipe, 'verify', return_value=(True, 'ok', 0.0, 0.0)), \
                patch.object(worker, 'block_max', return_value=0.0):
            row = worker.finish_one(job, dict(i=0, rel='refs2\\' + self.surface, ext='.png'))
        self.assertTrue(row['ok'], row)
        self.assertTrue(calls[0]['cycle_rgb'])
        self.assertEqual(row['recipe'], 'cycle_rgb')

    def test_mac_feeder_options_dry_run_has_no_remote_or_write(self):
        spec = importlib.util.spec_from_file_location('test_feeder', HERE.parent / 'mac/mac_feeder.py')
        feeder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(feeder)
        root = self.base / 'source'
        root.mkdir()
        (root / 'preview.png').write_bytes(b'header-not-needed')
        listing = self.base / 'list.txt'
        listing.write_text('preview.png\n', encoding='utf-8')
        with patch.object(sys, 'argv', ['mac_feeder.py', '--refs-root', str(root), '--list', str(listing), '--dry-run']), \
                patch.object(feeder, 'ssh', side_effect=AssertionError('SSH in dry-run')):
            self.assertEqual(feeder.main(), 0)
        self.assertEqual(feeder.REFS, root)

    def test_small_runner_defaults_and_dry_run(self):
        self.assertEqual(str(small_runner.parser().parse_args([]).refs_root), r'C:\Users\Leshe\Pictures\refs')
        with patch.object(small_runner.pipeline, 'select_rels', return_value=[]), \
                patch.object(small_runner.pipeline, 'WriterLock', side_effect=AssertionError('lock in dry-run')):
            self.assertEqual(small_runner.main(['--refs-root', str(self.src), '--list', str(self.inv),
                             '--library-root', str(self.lib), '--dry-run']), 0)


if __name__ == '__main__':
    unittest.main()
