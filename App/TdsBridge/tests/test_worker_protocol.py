import io
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


class WorkerProtocolTests(unittest.TestCase):
    def serve(self, commands, handler):
        from worker_protocol import serve
        output = io.StringIO()
        serve(io.StringIO('\n'.join(commands)), output, handler)
        return [json.loads(line) for line in output.getvalue().splitlines()]

    def test_consecutive_requests_and_logs_are_framed(self):
        calls = []
        def handle(op, path):
            calls.append((op, path))
            print('normal log including {json}')
        result = self.serve([
            json.dumps(dict(id='one', op='prepare', path='日本語.json')),
            json.dumps(dict(id='two', op='finish', path='日本語.json'))], handle)
        self.assertEqual(calls, [('prepare', '日本語.json'), ('finish', '日本語.json')])
        self.assertEqual([r['id'] for r in result if r['type'] == 'done'], ['one', 'two'])
        self.assertTrue(all(r['ok'] for r in result if r['type'] == 'done'))
        self.assertEqual(len([r for r in result if r['type'] == 'log']), 2)

    def test_error_does_not_become_success_or_break_next_request(self):
        def handle(op, path):
            if path == 'bad': raise ValueError('changed')
        result = self.serve([
            '{"id":"1","op":"prepare","path":"bad"}',
            '{"id":"2","op":"finish","path":"good"}'], handle)
        done = [r for r in result if r['type'] == 'done']
        self.assertFalse(done[0]['ok'])
        self.assertEqual(done[0]['error'], 'changed')
        self.assertTrue(done[1]['ok'])

    def test_invalid_command_is_rejected_without_call(self):
        calls = []
        result = self.serve(['{"id":"1","op":"delete","path":"x"}'], lambda *x: calls.append(x))
        self.assertEqual(calls, [])
        self.assertFalse(result[-1]['ok'])

    def test_eof_exits_without_work(self):
        self.assertEqual(self.serve([], lambda *x: self.fail('called')), [])
