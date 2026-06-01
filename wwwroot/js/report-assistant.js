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

        // ── Live-log processing steps (mirrors actual server pipeline) ──
        var RA_STEPS = [
            {
                id: 'ctx',
                icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/></svg>',
                label: 'Reading report context',
                sub: 'Loading KPIs, filters & dataset snapshot'
            },
            {
                id: 'plan',
                icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>',
                label: 'Planning data query',
                sub: 'Deciding if a CSV or recurring-ticket scan is needed'
            },
            {
                id: 'query',
                icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="3" width="18" height="18" rx="2"/><line x1="3" y1="9" x2="21" y2="9"/><line x1="9" y1="21" x2="9" y2="9"/></svg>',
                label: 'Querying report data',
                sub: 'Scanning CSV rows & applying active filters'
            },
            {
                id: 'ai',
                icon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 2a10 10 0 1 0 10 10"/><path d="M22 2 12 12"/><polyline points="22 2 15 2 22 9"/></svg>',
                label: 'Generating response',
                sub: 'Sending context + query results to AI model'
            }
        ];

        function makeBadge(cls, text) {
            return '<span class="ra-log-step-badge ' + cls + '">' + text + '</span>';
        }

        function makeStepIcon(svg, spinning) {
            var cls = spinning ? ' ra-step-icon-spin' : '';
            return '<span class="ra-log-step-icon' + cls + '" aria-hidden="true">' + svg + '</span>';
        }

        function showThinkingBubble() {
            var bubble = document.createElement('div');
            bubble.className = 'report-assistant-bubble assistant thinking';
            bubble.setAttribute('aria-busy', 'true');
            bubble.setAttribute('aria-label', 'Assistant is processing');

            var log = document.createElement('div');
            log.className = 'ra-log';

            // Header
            var header = document.createElement('div');
            header.className = 'ra-log-header';
            header.innerHTML = '<span class="ra-log-spinner" aria-hidden="true"></span>' +
                               '<span class="ra-log-title">Working…</span>';
            log.appendChild(header);

            var stepsEl = document.createElement('div');
            stepsEl.className = 'ra-log-steps';

            var stepEls = [];
            RA_STEPS.forEach(function (s) {
                var el = document.createElement('div');
                el.className = 'ra-log-step';
                el.id = 'ra-step-' + s.id + '-' + Date.now();
                el.innerHTML =
                    makeStepIcon(s.icon, false) +
                    '<span class="ra-log-step-body">' +
                        '<span class="ra-log-step-label">' + s.label + '</span>' +
                        '<span class="ra-log-step-sub">' + s.sub + '</span>' +
                    '</span>' +
                    makeBadge('ra-badge-pending', 'pending');
                stepsEl.appendChild(el);
                stepEls.push(el);
            });

            log.appendChild(stepsEl);
            bubble.appendChild(log);
            messagesEl.appendChild(bubble);
            scrollMessages();

            // Animate steps in one-by-one
            var DELAYS = [0, 90, 190, 300];
            stepEls.forEach(function (el, i) {
                setTimeout(function () {
                    el.classList.add('ra-step-visible');
                }, DELAYS[i] || i * 90);
            });

            // Activate each step progressively (simulate pipeline progression)
            var ACTIVE_DELAYS = [60, 800, 2200, 4000];

            function activateStep(idx) {
                if (!bubble.parentNode) return; // already removed
                var el = stepEls[idx];
                // Mark previous as done
                if (idx > 0) {
                    var prev = stepEls[idx - 1];
                    prev.classList.remove('ra-step-active');
                    prev.classList.add('ra-step-done');
                    var prevBadge = prev.querySelector('.ra-log-step-badge');
                    if (prevBadge) { prevBadge.className = 'ra-log-step-badge ra-badge-done'; prevBadge.textContent = 'done'; }
                    // stop spin on prev icon
                    var prevIcon = prev.querySelector('.ra-log-step-icon');
                    if (prevIcon) prevIcon.classList.remove('ra-step-icon-spin');
                }
                el.classList.add('ra-step-active');
                // animate icon spinning while active
                var icon = el.querySelector('.ra-log-step-icon');
                if (icon) icon.classList.add('ra-step-icon-spin');
                var badge = el.querySelector('.ra-log-step-badge');
                if (badge) { badge.className = 'ra-log-step-badge ra-badge-running'; badge.textContent = 'running'; }
            }

            var activeTimers = [];
            ACTIVE_DELAYS.forEach(function (delay, idx) {
                var t = setTimeout(function () { activateStep(idx); }, delay);
                activeTimers.push(t);
            });

            bubble._raTimers = activeTimers;
            bubble._raStepEls = stepEls;
            return bubble;
        }

        function finalizeThinkingBubble(bubble) {
            // Mark last active step as done, clear timers
            if (!bubble) return;
            if (bubble._raTimers) bubble._raTimers.forEach(clearTimeout);
            var stepEls = bubble._raStepEls || [];
            stepEls.forEach(function (el) {
                el.classList.remove('ra-step-active');
                el.classList.add('ra-step-done', 'ra-step-visible');
                var badge = el.querySelector('.ra-log-step-badge');
                if (badge) { badge.className = 'ra-log-step-badge ra-badge-done'; badge.textContent = 'done'; }
                var icon = el.querySelector('.ra-log-step-icon');
                if (icon) icon.classList.remove('ra-step-icon-spin');
            });
            // stop header spinner
            var spinner = bubble.querySelector('.ra-log-spinner');
            if (spinner) spinner.style.animation = 'none';
            var title = bubble.querySelector('.ra-log-title');
            if (title) title.textContent = 'Done';
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

                // Flash "all done" on the log for a moment before replacing
                finalizeThinkingBubble(thinkingEl);
                await new Promise(function (r) { setTimeout(r, 280); });
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
                finalizeThinkingBubble(thinkingEl);
                setTimeout(function () { removeThinkingBubble(thinkingEl); }, 180);
                thinkingEl = null;
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
