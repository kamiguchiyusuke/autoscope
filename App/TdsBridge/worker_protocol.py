"""Line-framed, serial requests over private parent-owned pipes. No sockets."""
import contextlib
import io
import json


def serve(reader, writer, handler):
    def emit(value):
        writer.write(json.dumps(value, ensure_ascii=False) + '\n')
        writer.flush()

    class Logs(io.TextIOBase):
        def __init__(self, request_id):
            self.request_id, self.pending = request_id, ''

        def write(self, value):
            self.pending += value
            while '\n' in self.pending:
                line, self.pending = self.pending.split('\n', 1)
                emit(dict(type='log', id=self.request_id, text=line))
            return len(value)

        def flush(self):
            if self.pending:
                emit(dict(type='log', id=self.request_id, text=self.pending))
                self.pending = ''

    for line in reader:
        request_id = ''
        try:
            q = json.loads(line)
            request_id = q['id']
            if (not isinstance(request_id, str) or not request_id or
                    q['op'] not in ('prepare', 'finish') or
                    not isinstance(q['path'], str) or not q['path']):
                raise ValueError('Invalid worker request')
            logs = Logs(request_id)
            try:
                with contextlib.redirect_stdout(logs), contextlib.redirect_stderr(logs):
                    handler(q['op'], q['path'])
            finally:
                logs.flush()
            emit(dict(type='done', id=request_id, ok=True))
        except Exception as e:
            emit(dict(type='done', id=request_id, ok=False, error=str(e)))
