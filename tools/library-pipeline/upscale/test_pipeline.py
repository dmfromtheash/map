"""Isolated resilience tests. GPU is mocked; image tests use only owner fixtures."""
from concurrent.futures import ThreadPoolExecutor
from concurrent.futures.process import BrokenProcessPool
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
import subprocess
import sys

import run_upscale2 as pipeline
import progress
import rerun_outliers

BASE = Path(__file__).resolve().parent
# Owner fixtures: samples/ next to the deployed copy (H:\MapLibrary\sol\sol1), otherwise the same files from the
# repository (tests/fixtures/assets, identical md5).
SAMPLES = BASE / 'samples'
if not (SAMPLES / 'owner-mountain-01.png').is_file():
    SAMPLES = BASE.parents[2] / 'tests/fixtures/assets'


def crash_finish_worker(*args):
    import os
    os._exit(37)


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='test-', dir=BASE)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.config = pipeline.Config(self.root, workers=2, batch=1)

    def write(self, rows):
        for row in rows:
            pipeline.append_record(self.config.manifest, row)

    def test_resume_any_success_and_partial_record(self):
        self.write([{'rel': 'one.png', 'ok': True}, {'rel': 'one.png', 'ok': False}])
        with self.config.manifest.open('ab') as stream:
            stream.write(b'{"rel":')
        self.write([{'rel': 'two.png', 'ok': True}])
        self.assertEqual(pipeline.done_set(self.config.manifest), {'one.png', 'two.png'})
        listing = self.root / 'list.txt'
        listing.write_text('one.png\none.png\nthree.png\nmissing.png\n', encoding='utf-8')
        (self.root / 'three.png').touch()
        args = type('Args', (), dict(list=listing, refs_root=self.root, limit=None))()
        self.assertEqual(pipeline.select_rels(args, pipeline.done_set(self.config.manifest)), ['three.png'])

    def test_retry_once_alone_and_failed_journal(self):
        calls = []
        def gpu(root, rels, folder):
            calls.append(list(rels))
            return [(i, rel) for i, rel in enumerate(rels)], []
        attempts = {}
        def finish(root, rel, index, folder, config):
            attempts[rel] = attempts.get(rel, 0) + 1
            return {'rel': rel, 'ok': rel == 'recovered.png' and attempts[rel] == 2, 'msg': 'ERR test'}
        config = pipeline.Config(self.root, workers=2, batch=2)
        with patch.object(pipeline, 'prepare_gpu', gpu), patch.object(pipeline, 'finish_one', finish), \
                patch.object(pipeline, 'ProcessPoolExecutor', ThreadPoolExecutor):
            # Fake producer creates dirs so normal cleanup is also exercised.
            def with_dirs(*args):
                Path(args[2]).mkdir(parents=True)
                return gpu(*args)
            with patch.object(pipeline, 'prepare_gpu', with_dirs):
                result = pipeline.run(self.root, ['recovered.png', 'bad.png'], config)
        self.assertEqual(calls[0], ['recovered.png', 'bad.png'])
        self.assertCountEqual(calls[1:], [['recovered.png'], ['bad.png']])
        self.assertEqual(attempts, {'recovered.png': 2, 'bad.png': 2})
        self.assertEqual((result['ok'], result['bad']), (1, 1))
        self.assertEqual([row['rel'] for row in pipeline.records(config.failed)], ['bad.png'])
        self.assertEqual(len(list(pipeline.records(config.manifest))), 4)

    def test_gpu_batch_exception_gets_two_single_retries(self):
        config = pipeline.Config(self.root, workers=1, batch=2)
        def gpu(root, rels, folder):
            Path(folder).mkdir(parents=True)
            if len(rels) == 2:
                return [], [pipeline.error_record(rel, 'gpu failed') for rel in rels]
            return [(0, rels[0])], []
        with patch.object(pipeline, 'prepare_gpu', gpu), \
                patch.object(pipeline, 'finish_one', lambda root, rel, *a: {'rel': rel, 'ok': True}), \
                patch.object(pipeline, 'ProcessPoolExecutor', ThreadPoolExecutor):
            result = pipeline.run(self.root, ['a.png', 'b.png'], config)
        self.assertEqual((result['ok'], result['bad']), (2, 0))
        self.assertFalse(config.failed.exists())

    def test_workers_default_matches_legacy(self):
        self.assertEqual(pipeline.Config().workers, 6)
        self.assertEqual(pipeline.parser().parse_args(['refs', 'list']).workers, 6)
        with patch.object(rerun_outliers, 'run', return_value={'bad': 0}) as run:
            self.assertEqual(rerun_outliers.main([str(self.root), '--library-root', str(self.root)]), 0)
        config = run.call_args.args[2]
        self.assertEqual(config.workers, 6)
        self.assertTrue(config.rerun)

    def test_measured_rejection_is_not_retried(self):
        calls = []
        def gpu(root, rels, folder):
            Path(folder).mkdir(parents=True)
            calls.append(list(rels))
            return [(0, rels[0])], []
        with patch.object(pipeline, 'prepare_gpu', gpu), \
                patch.object(pipeline, 'finish_one', return_value={
                    'rel': 'rejected.png', 'ok': False, 'msg': 'alphaD=1 colourD=20',
                    'alphaD': 1, 'colourD': 20}), \
                patch.object(pipeline, 'ProcessPoolExecutor', ThreadPoolExecutor):
            result = pipeline.run(self.root, ['rejected.png'], self.config)
        self.assertEqual(calls, [['rejected.png']])
        self.assertEqual((result['files'], result['ok'], result['bad']), (1, 0, 1))
        self.assertEqual(len(list(pipeline.records(self.config.manifest))), 1)
        self.assertEqual([row['attempt'] for row in pipeline.records(self.config.failed)], [1])

    def test_broken_pool_aborts_initial_and_retry_finish(self):
        for retry in (False, True):
            with self.subTest(retry=retry):
                library = self.root / str(retry)
                calls = []
                def gpu(root, rels, folder):
                    Path(folder).mkdir(parents=True)
                    calls.append(list(rels))
                    return [(0, rels[0])], []
                def finish(root, rel, index, folder, config):
                    if retry and Path(folder).name.startswith('b'):
                        return pipeline.error_record(rel, 'transient')
                    raise BrokenProcessPool('worker exited')
                config = pipeline.Config(library, workers=1, batch=1)
                with patch.object(pipeline, 'prepare_gpu', gpu), \
                        patch.object(pipeline, 'finish_one', finish), \
                        patch.object(pipeline, 'ProcessPoolExecutor', ThreadPoolExecutor):
                    with self.assertRaises(BrokenProcessPool):
                        pipeline.run(self.root, ['a.png', 'b.png', 'c.png'], config)
                self.assertNotIn(['c.png'], calls)
                self.assertFalse(config.failed.exists())
                self.assertEqual(len(list(pipeline.records(config.manifest))), int(retry))
                self.assertEqual(list((library / 'work').iterdir()), [])

    def test_broken_pool_during_retry_submission_is_not_file_error(self):
        class Pool(ThreadPoolExecutor):
            submissions = 0
            def submit(self, *args, **kwargs):
                self.submissions += 1
                if self.submissions == 2:
                    raise BrokenProcessPool('cannot submit to a broken pool')
                return super().submit(*args, **kwargs)
        def gpu(root, rels, folder):
            Path(folder).mkdir(parents=True)
            return [(0, rels[0])], []
        with patch.object(pipeline, 'prepare_gpu', gpu), \
                patch.object(pipeline, 'finish_one', return_value=pipeline.error_record('a.png', 'transient')), \
                patch.object(pipeline, 'ProcessPoolExecutor', Pool):
            with self.assertRaises(BrokenProcessPool):
                pipeline.run(self.root, ['a.png'], self.config)
        self.assertFalse(self.config.failed.exists())
        self.assertEqual(len(list(pipeline.records(self.config.manifest))), 1)

    def test_real_worker_exit_aborts_instead_of_marking_files_bad(self):
        def gpu(root, rels, folder):
            Path(folder).mkdir(parents=True)
            return [(0, rels[0])], []
        with patch.object(pipeline, 'prepare_gpu', gpu), \
                patch.object(pipeline, 'finish_one', crash_finish_worker):
            with self.assertRaises(BrokenProcessPool):
                pipeline.run(self.root, ['a.png'], pipeline.Config(self.root, workers=1))
        self.assertFalse(self.config.manifest.exists())
        self.assertFalse(self.config.failed.exists())
        self.assertEqual(list((self.root / 'work').iterdir()), [])

    def test_actual_overlap_and_serial_gpu(self):
        cpu_started, gpu_next_started = threading.Event(), threading.Event()
        gpu_active = threading.Lock()
        def gpu(root, rels, folder):
            self.assertTrue(gpu_active.acquire(blocking=False))
            try:
                Path(folder).mkdir(parents=True)
                if rels == ['second.png']:
                    self.assertTrue(cpu_started.wait(5), 'GPU k+1 did not overlap CPU k')
                    gpu_next_started.set()
                return [(0, rels[0])], []
            finally:
                gpu_active.release()
        def finish(root, rel, *args):
            if rel == 'first.png':
                cpu_started.set()
                self.assertTrue(gpu_next_started.wait(5), 'CPU k blocked GPU k+1')
            return {'rel': rel, 'ok': True}
        with patch.object(pipeline, 'prepare_gpu', gpu), patch.object(pipeline, 'finish_one', finish), \
                patch.object(pipeline, 'ProcessPoolExecutor', ThreadPoolExecutor):
            result = pipeline.run(self.root, ['first.png', 'second.png'], self.config)
        self.assertEqual(result['ok'], 2)

    def test_outliers_thresholds_history_and_success(self):
        self.write([{'rel': 'good.png', 'ok': True},
                    {'rel': 'good.png', 'ok': False, 'colourD': 20},
                    {'rel': 'colour.png', 'ok': False, 'colourD': 10.01},
                    {'rel': 'colour.png', 'ok': False, 'msg': 'ERR'},
                    {'rel': 'alpha.png', 'ok': False, 'alphaD': 8.01},
                    {'rel': 'edge.png', 'ok': False, 'colourD': 10, 'alphaD': 8}])
        self.assertCountEqual(rerun_outliers.select_outliers(self.config.manifest), ['colour.png', 'alpha.png'])

    def test_delete_only_after_durable_success(self):
        source = self.root / 'source.png'
        source.touch()
        config = pipeline.Config(self.root, delete=True)
        with patch.object(pipeline, 'append_record', side_effect=OSError('disk full')):
            with self.assertRaises(OSError):
                pipeline.persist({'rel': source.name, 'ok': True}, 1, config, self.root)
        self.assertTrue(source.exists())
        pipeline.persist({'rel': source.name, 'ok': True}, 1, config, self.root)
        self.assertFalse(source.exists())
        rows = list(pipeline.records(config.manifest))
        self.assertEqual(len(rows), 1)
        self.assertTrue(rows[0]['ok'])

    def test_writer_lock_excludes_second_writer(self):
        with pipeline.WriterLock(self.root):
            with self.assertRaises(RuntimeError):
                with pipeline.WriterLock(self.root):
                    self.fail('second writer acquired the lock')
        with pipeline.WriterLock(self.root):
            pass

    def test_path_traversal(self):
        for rel in ('../x.png', r'C:\x.png', r'\\server\share\x.png', ''):
            with self.assertRaises(ValueError):
                pipeline.relative_path(rel)

    def test_gpu_failure_and_prepare_error_are_file_records(self):
        source = SAMPLES / 'owner-mountain-01.png'
        with patch.object(pipeline.subprocess, 'run', return_value=type('Exit', (), {'returncode': 17})()):
            ready, errors = pipeline.prepare_gpu(source.parent, [source.name, 'missing.png'], self.root / 'batch')
        self.assertEqual(ready, [])
        self.assertEqual(len(errors), 2)
        self.assertTrue(any('exit 17' in row['msg'] for row in errors))

    def test_real_recipe_atomic_output_and_safer_parameters(self):
        # CPU-only test uses owner source enlarged with Lanczos as model stand-ins.
        from PIL import Image
        source = SAMPLES / 'owner-mountain-01.png'
        model = self.root / 'models'
        for sub in ('a', 'b'):
            (model / sub).mkdir(parents=True)
            with Image.open(source) as image:
                image.convert('RGB').resize((2048, 2048), Image.Resampling.LANCZOS).save(model / sub / '0.png')
        config = pipeline.Config(self.root, anime_weight=.8, grain=.3)
        row = pipeline.finish_one(source.parent, source.name, 0, model, config)
        self.assertNotIn('ERR', row['msg'])
        self.assertEqual((row['animeWeight'], row['grain'], row['out']), (.8, .3, [2048, 2048]))
        output = self.root / 'upscaled' / source.with_suffix('.webp').name
        self.assertTrue(output.exists())
        before = output.read_bytes()
        with patch.object(pipeline.recipe, 'finish', side_effect=OSError('encode failed')):
            failed = pipeline.finish_one(source.parent, source.name, 0, model, config)
        self.assertFalse(failed['ok'])
        self.assertEqual(output.read_bytes(), before)
        self.assertEqual(len(list(output.parent.glob('*.webp'))), 1)

    def fake_finish(self, rel, *, grain=None, rerun=False, metrics=None, previous=False):
        """Use only an owner fixture as source; replace codec/verify for fault cases."""
        from PIL import Image
        source = self.root / 'refs' / rel
        source.parent.mkdir(parents=True, exist_ok=True)
        source.write_bytes((SAMPLES / 'owner-mountain-01.png').read_bytes())
        target = self.root / 'upscaled' / Path(rel).with_suffix('.webp')
        if previous:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b'previous-output')
        paths = []
        def finish(src, a, b, out, factor, **kwargs):
            paths.append(Path(out))
            self.assertTrue(str(out).endswith('.webp.tmp'))
            self.assertNotIn(Path(out), list(target.parent.glob('*.webp')))
            Path(out).write_bytes(b'candidate-output')
            return tuple(side * factor for side in src.size)
        with Image.open(source) as image:
            original_size = image.size
        config = pipeline.Config(self.root, grain=grain, rerun=rerun)
        with patch.object(pipeline.recipe, 'finish', side_effect=finish) as encode, \
                patch.object(pipeline.recipe, 'verify', side_effect=metrics or [(True, 'ok', 0, 0)]):
            row = pipeline.finish_one(self.root / 'refs', rel, 0, self.root / 'models', config)
        self.assertFalse(any(path.exists() for path in paths))
        self.assertEqual(row['out'], [side * pipeline.factor_for(max(original_size)) for side in original_size])
        self.assertEqual(source.read_bytes(), (SAMPLES / 'owner-mountain-01.png').read_bytes())
        return row, target, encode.call_args.kwargs

    def test_grain_never_exceeds_normal_recipe(self):
        for rel, grain, expected in (
                ('Battlemaps Classic/fixture.png', .3, .15),
                ('Battlemaps Classic/fixture.png', None, .15),
                ('Battlemaps Classic/fixture.png', .1, .1),
                ('fixture.png', .3, .3), ('fixture.png', None, .6)):
            with self.subTest(rel=rel, grain=grain):
                row, target, kwargs = self.fake_finish(rel, grain=grain)
                self.assertEqual(row['grain'], expected)
                self.assertEqual(kwargs['grain_k'], expected)
                target.unlink()

    def test_rerun_replaces_only_success_or_both_metrics_improve(self):
        cases = (
            ((True, 'ok', 1, 2), True),
            ((False, 'both better', 8, 15), True),
            ((False, 'colour worse', 8, 21), False),
            ((False, 'alpha worse', 10, 15), False),
            ((False, 'equal', 9, 20), False),
        )
        for number, (candidate, replace) in enumerate(cases):
            with self.subTest(candidate=candidate):
                row, target, _ = self.fake_finish(f'case{number}.png', rerun=True, grain=.3,
                    metrics=[candidate, (False, 'previous', 9, 20)], previous=True)
                if replace:
                    self.assertEqual(target.read_bytes(), b'candidate-output')
                    self.assertFalse(target.with_suffix('.rerun.webp').exists())
                    self.assertEqual((row['alphaD'], row['colourD']), candidate[2:])
                else:
                    self.assertEqual(target.read_bytes(), b'previous-output')
                    self.assertEqual(target.with_suffix('.rerun.webp').read_bytes(), b'candidate-output')
                    self.assertEqual((row['alphaD'], row['colourD']), (9, 20))
                    self.assertEqual(row['bytes'], len(b'previous-output'))
                    self.assertEqual(row['rerunCandidate']['path'], f'case{number}.rerun.webp')
                    self.assertEqual(row['rerunCandidate']['alphaD'], candidate[2])
                    self.assertEqual(row['rerunCandidate']['colourD'], candidate[3])
                    self.assertNotIn('grain', row)
                    self.assertNotIn('animeWeight', row)
                    pipeline.persist(row, 1, self.config, self.root)
                    latest = list(pipeline.records(self.config.manifest))[-1]
                    self.assertEqual((latest['alphaD'], latest['colourD']), (9, 20))
                    self.assertIn(row['rel'], rerun_outliers.select_outliers(self.config.manifest))

    def test_rerun_without_previous_output_uses_normal_path(self):
        row, target, _ = self.fake_finish('missing-output.png', rerun=True,
                                         metrics=[(False, 'rejected', 9, 20)])
        self.assertEqual(target.read_bytes(), b'candidate-output')
        self.assertFalse(row['ok'])

    def test_progress_text_is_utf8_even_with_cp1251_environment(self):
        import os
        listing = self.root / 'list.txt'
        listing.write_text('fixture.png\n', encoding='utf-8')
        result = subprocess.run([sys.executable, '-B', str(BASE / 'progress.py'),
            '--library-root', str(self.root), '--list', str(listing)],
            capture_output=True, env=dict(os.environ, PYTHONIOENCODING='cp1251'), check=True)
        self.assertIn('Готово: 0/1', result.stdout.decode('utf-8'))

    def test_progress_deduplicates_and_filters_hour(self):
        now = 1800000000
        def stamp(delta):
            return datetime.fromtimestamp(now + delta, timezone.utc).isoformat()
        self.write([{'rel': 'a.png', 'ok': True, 'completedAt': stamp(-60)},
                    {'rel': 'a.png', 'ok': True, 'completedAt': stamp(-60), 'sourceDeleted': True},
                    {'rel': 'b.png', 'ok': True, 'completedAt': stamp(-3601)},
                    {'rel': 'bad.png', 'ok': False}, {'rel': 'bad.png', 'ok': False},
                    {'rel': 'outside.png', 'ok': True, 'completedAt': stamp(-60)}])
        listing = self.root / 'all.txt'
        listing.write_text('a.png\nb.png\nbad.png\npending.png\n', encoding='utf-8')
        state = progress.snapshot(self.root, listing, now)
        self.assertEqual((state['done'], state['total'], state['bad'], state['filesPerHour']), (2, 4, 1, 1))
        self.assertEqual(state['etaHours'], 2)
        self.assertGreater(state['diskBytes'], 0)

    def test_legacy_last_hour_includes_idle_time(self):
        import os
        log = self.root / 'upscale.log'
        log.write_text('todo 3000 (done 84)\n1000/3000 bad=0 elapsed=1.00h\n2000/3000 bad=0 elapsed=2.00h\n')
        os.utime(log, (10000, 10000))
        self.assertEqual(progress.legacy_hourly(log, 10000), 1000)
        self.assertEqual(progress.legacy_hourly(log, 11800), 500)
        self.assertEqual(progress.legacy_hourly(log, 13601), 0)

    def test_v2_log_is_not_counted_again(self):
        log = self.root / 'upscale.log'
        log.write_text('pipeline=v2 startedAt=10000\ntodo 100 (done 0)\n100/100 bad=0 elapsed=1.00h\n')
        self.assertIsNone(progress.legacy_hourly(log, 10000))
        log.write_text('todo 3000 (done 84)\n1000/3000 bad=0 elapsed=1.00h\n'
                       '2000/3000 bad=0 elapsed=2.00h\npipeline=v2 startedAt=10000\n'
                       'todo 1000 (done 2084)\n100/1000 bad=0 elapsed=1.00h\n')
        self.assertEqual(progress.legacy_hourly(log, 11800), 500)


if __name__ == '__main__':
    unittest.main(verbosity=2)
