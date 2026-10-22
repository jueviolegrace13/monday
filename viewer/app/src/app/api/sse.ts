/** One server-sent event: its name (default "message") and its data lines joined with "\n". */
export interface SseFrame {
  event: string;
  data: string;
}

/**
 * Reads a server-sent event stream (https://html.spec.whatwg.org/multipage/server-sent-events.html).
 * Handles LF, CRLF and CR line ends, comments, multi-line data, frames split across chunks and a
 * final frame with no blank line after it. Ends when the stream ends or the signal aborts.
 */
export async function* readSse(
  body: ReadableStream<Uint8Array>,
  signal?: AbortSignal,
): AsyncGenerator<SseFrame> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const stop = () => void reader.cancel().catch(() => undefined);
  signal?.addEventListener('abort', stop, { once: true });

  let buffer = '';
  let event = '';
  let data: string[] = [];

  const flush = (): SseFrame | null => {
    const frame = data.length > 0 ? { event: event || 'message', data: data.join('\n') } : null;
    event = '';
    data = [];
    return frame;
  };

  const line = (text: string): SseFrame | null => {
    if (text === '') return flush();
    if (text.startsWith(':')) return null;
    const colon = text.indexOf(':');
    const field = colon < 0 ? text : text.slice(0, colon);
    let value = colon < 0 ? '' : text.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);
    if (field === 'event') event = value;
    else if (field === 'data') data.push(value);
    return null;
  };

  try {
    while (!signal?.aborted) {
      let chunk: ReadableStreamReadResult<Uint8Array>;
      try {
        chunk = await reader.read();
      } catch (error) {
        if (signal?.aborted) return;
        throw error;
      }
      if (signal?.aborted) return;
      buffer += decoder.decode(chunk.value, { stream: !chunk.done });

      for (;;) {
        const end = buffer.search(/[\r\n]/);
        if (end < 0) break;
        // A CR at the very end may be the first half of a CRLF: wait for the next chunk.
        if (buffer[end] === '\r' && end === buffer.length - 1 && !chunk.done) break;
        const text = buffer.slice(0, end);
        buffer = buffer.slice(buffer[end] === '\r' && buffer[end + 1] === '\n' ? end + 2 : end + 1);
        const frame = line(text);
        if (frame) yield frame;
      }

      if (chunk.done) {
        if (buffer !== '') {
          const frame = line(buffer);
          if (frame) yield frame;
        }
        const last = flush();
        if (last) yield last;
        return;
      }
    }
  } finally {
    signal?.removeEventListener('abort', stop);
    stop();
  }
}
