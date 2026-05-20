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
        var isBusy = false;

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

        function setComposerEnabled(enabled) {
            if (sendBtn) sendBtn.disabled = !enabled;
            if (input) input.disabled = !enabled;
        }

        function scrollMessages() {
            if (messagesEl) messagesEl.scrollTop = messagesEl.scrollHeight;
        }

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
            scrollMessages();
        }

        function showThinkingBubble() {
            var div = document.createElement('div');
            div.className = 'report-assistant-bubble assistant thinking';
            div.setAttribute('aria-busy', 'true');
            div.setAttribute('aria-label', 'Assistant is thinking');
            div.innerHTML =
                '<span class="report-assistant-thinking" aria-hidden="true">' +
                '<span></span><span></span><span></span>' +
                '</span>';
            messagesEl.appendChild(div);
            scrollMessages();
            return div;
        }

        function removeThinkingBubble(el) {
            if (el && el.parentNode) el.parentNode.removeChild(el);
        }

        function typingChunkSize(total) {
            if (total > 1200) return 8;
            if (total > 600) return 4;
            if (total > 250) return 2;
            return 1;
        }

        function typingDelay(char, total) {
            if (total > 800) return 6;
            if (total > 400) return 10;
            if (char === '\n') return 28;
            if ('.!?'.indexOf(char) >= 0) return 36;
            return 16;
        }

        function typeAssistantBubble(text, isErr) {
            return new Promise(function (resolve) {
                var div = document.createElement('div');
                div.className = 'report-assistant-bubble assistant is-typing' + (isErr ? ' err' : '');

                var textSpan = document.createElement('span');
                textSpan.className = 'report-assistant-bubble-text';

                var cursor = document.createElement('span');
                cursor.className = 'report-assistant-cursor';
                cursor.setAttribute('aria-hidden', 'true');

                div.appendChild(textSpan);
                div.appendChild(cursor);
                messagesEl.appendChild(div);
                scrollMessages();

                var i = 0;
                var chunk = typingChunkSize(text.length);

                function tick() {
                    if (i >= text.length) {
                        div.classList.remove('is-typing');
                        cursor.remove();
                        scrollMessages();
                        resolve();
                        return;
                    }

                    var next = Math.min(i + chunk, text.length);
                    textSpan.textContent = text.slice(0, next);
                    i = next;
                    scrollMessages();

                    var lastChar = text.charAt(i - 1);
                    setTimeout(tick, typingDelay(lastChar, text.length));
                }

                tick();
            });
        }

        if (messagesEl && history.length === 0) {
            appendBubble('assistant', 'Ask me anything — general questions or numbers from this page’s report. Greetings get an instant reply.', false);
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
            if (!input || !sendBtn || isBusy) return;
            var text = (input.value || '').trim();
            if (!text) return;

            isBusy = true;
            setComposerEnabled(false);
            appendBubble('user', text, false);
            input.value = '';

            var thinkingEl = showThinkingBubble();

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
                removeThinkingBubble(thinkingEl);
                thinkingEl = null;

                if (!res.ok) {
                    var errMsg = (data && data.reply) ? data.reply : 'Request failed (' + res.status + ').';
                    appendBubble('assistant', errMsg, true);
                    return;
                }

                if (data && data.reply) {
                    history.push({ role: 'user', content: text });
                    history.push({ role: 'assistant', content: data.reply });
                    if (history.length > 40) history = history.slice(-40);
                    await typeAssistantBubble(data.reply, false);
                } else {
                    appendBubble('assistant', 'No reply from server.', true);
                }
            } catch (e) {
                removeThinkingBubble(thinkingEl);
                appendBubble('assistant', 'Network error. Try again.', true);
            } finally {
                isBusy = false;
                setComposerEnabled(true);
                if (input) input.focus();
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
