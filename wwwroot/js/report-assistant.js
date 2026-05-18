(function () {
    function ready(fn) {
        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', fn);
        else fn();
    }

    ready(function () {
        var root = document.getElementById('report-assistant-root');
        if (!root) return;

        var fab = document.getElementById('report-assistant-fab');
        var panel = document.getElementById('report-assistant-panel');
        var closeBtn = document.getElementById('report-assistant-close');
        var messagesEl = document.getElementById('report-assistant-messages');
        var input = document.getElementById('report-assistant-input');
        var sendBtn = document.getElementById('report-assistant-send');

        var open = false;
        var history = [];

        function pageKind() { return root.getAttribute('data-page-kind') || 'upload'; }
        function tokenVal() {
            var t = root.getAttribute('data-token') || '';
            return t.trim() === '' ? null : t.trim();
        }
        function viewVal() {
            var v = root.getAttribute('data-view') || '';
            return v.trim() === '' ? null : v.trim();
        }
        function csrf() { return root.getAttribute('data-csrf') || ''; }

        function setOpen(v) {
            open = v;
            root.classList.toggle('is-open', v);
            if (panel) {
                panel.classList.toggle('open', v);
                panel.setAttribute('aria-hidden', v ? 'false' : 'true');
            }
            if (fab) {
                fab.setAttribute('aria-expanded', v ? 'true' : 'false');
                fab.setAttribute('aria-label', v ? 'Close report assistant' : 'Open report assistant');
            }
            if (v && input) setTimeout(function () { input.focus(); }, 80);
        }

        function appendBubble(role, text, isErr) {
            var div = document.createElement('div');
            div.className = 'report-assistant-bubble ' + role + (isErr ? ' err' : '');
            div.textContent = text;
            messagesEl.appendChild(div);
            messagesEl.scrollTop = messagesEl.scrollHeight;
        }

        if (messagesEl && history.length === 0) {
            appendBubble('assistant', 'Ask a question about the numbers and filters for this page. Greetings get an instant reply.', false);
        }

        function toggle() { setOpen(!open); }

        if (fab) fab.addEventListener('click', toggle);
        if (closeBtn) closeBtn.addEventListener('click', function () { setOpen(false); });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && open) setOpen(false);
        });

        function buildPayload(userText) {
            return {
                messages: history.concat([{ role: 'user', content: userText }]),
                pageKind: pageKind(),
                token: tokenVal(),
                view: viewVal()
            };
        }

        async function send() {
            if (!input || !sendBtn) return;
            var text = (input.value || '').trim();
            if (!text) return;

            sendBtn.disabled = true;
            appendBubble('user', text, false);
            input.value = '';

            try {
                var res = await fetch('/Report/Assistant/Chat', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: {
                        'Content-Type': 'application/json',
                        'X-CSRF-TOKEN': csrf()
                    },
                    body: JSON.stringify(buildPayload(text))
                });

                var data = await res.json().catch(function () { return null; });
                if (!res.ok) {
                    appendBubble('assistant', (data && data.reply) ? data.reply : 'Request failed (' + res.status + ').', true);
                    return;
                }

                if (data && data.reply) {
                    history.push({ role: 'user', content: text });
                    history.push({ role: 'assistant', content: data.reply });
                    if (history.length > 40) history = history.slice(-40);
                    appendBubble('assistant', data.reply, false);
                } else {
                    appendBubble('assistant', 'No reply from server.', true);
                }
            } catch (e) {
                appendBubble('assistant', 'Network error. Try again.', true);
            } finally {
                sendBtn.disabled = false;
            }
        }

        if (sendBtn) sendBtn.addEventListener('click', send);
        if (input) input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                send();
            }
        });
    });
})();
