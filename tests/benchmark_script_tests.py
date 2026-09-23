import contextlib, io, json, runpy, unittest, urllib.request
from pathlib import Path
from unittest.mock import patch

class BenchmarkScriptTests(unittest.TestCase):
    def test_qualified_identity_drives_actual_requests(self):
        calls = []
        def open_fake(req, **kwargs):
            if req.full_url.endswith('/api/tags'):
                return io.BytesIO(json.dumps({'models': [
                    {'name':'RENAMED DISPLAY', 'model':'deepseek-v4-flash@deepseek:latest'},
                    {'name':'OTHER DISPLAY', 'model':'deepseek-v4-flash@ollama:latest'}
                ]}).encode())
            calls.append(json.loads(req.data)['model'])
            return io.BytesIO(b'{"message":{"content":"pong"}}')
        with patch.object(urllib.request, 'urlopen', open_fake), contextlib.redirect_stdout(io.StringIO()):
            runpy.run_path(str(Path(__file__).parents[1] / 'scripts/comprehensive-benchmark.py'))
        self.assertEqual(calls, ['deepseek-v4-flash@deepseek']*3 + ['deepseek-v4-flash@ollama']*3)

if __name__ == '__main__': unittest.main()
