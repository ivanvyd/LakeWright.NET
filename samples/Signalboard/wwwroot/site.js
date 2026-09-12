(() => {
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const horizontalTabs = window.matchMedia('(min-width: 521px) and (max-width: 820px)');
    let ambientPaused = false;
    let ambientObserver;
    const entrance = [
        { opacity: 0.3, transform: 'translateY(16px)' },
        { opacity: 1, transform: 'translateY(0)' },
    ];
    const entranceTiming = { duration: 650, easing: 'cubic-bezier(.2, .7, .2, 1)' };

    const initializeBoundaryLabs = () => {
        document.querySelectorAll('[data-boundary-lab]:not([data-ready])').forEach(lab => {
            lab.dataset.ready = 'true';
            const tabs = Array.from(lab.querySelectorAll('[data-boundary-tab]'));

            const activate = tab => {
                tabs.forEach(candidate => {
                    const selected = candidate === tab;
                    candidate.setAttribute('aria-selected', String(selected));
                    candidate.tabIndex = selected ? 0 : -1;

                    const panel = document.getElementById(candidate.getAttribute('aria-controls'));
                    if (panel) {
                        panel.hidden = !selected;
                        if (selected && !reducedMotion.matches) {
                            panel.getAnimations().forEach(animation => animation.cancel());
                            panel.animate(
                                [{ opacity: .4, transform: 'translateX(8px)' }, { opacity: 1, transform: 'translateX(0)' }],
                                { duration: 240, easing: entranceTiming.easing });
                        }
                    }
                });
            };

            tabs.forEach((tab, index) => {
                tab.addEventListener('click', () => activate(tab));
                tab.addEventListener('keydown', event => {
                    let nextIndex = null;

                    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') nextIndex = (index + 1) % tabs.length;
                    if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') nextIndex = (index - 1 + tabs.length) % tabs.length;
                    if (event.key === 'Home') nextIndex = 0;
                    if (event.key === 'End') nextIndex = tabs.length - 1;
                    if (nextIndex === null) return;

                    event.preventDefault();
                    activate(tabs[nextIndex]);
                    tabs[nextIndex].focus();
                });
            });
        });
    };

    const initializeCopyButtons = () => {
        document.querySelectorAll('[data-copy-target]:not([data-ready])').forEach(button => {
            button.dataset.ready = 'true';
            const defaultLabel = button.textContent;

            button.addEventListener('click', async () => {
                const target = document.getElementById(button.dataset.copyTarget);
                const status = document.getElementById(button.dataset.copyStatus);
                if (!target) return;

                const resetLabel = () => {
                    window.setTimeout(() => { button.textContent = defaultLabel; }, 1600);
                };

                const selectText = () => {
                    const range = document.createRange();
                    range.selectNodeContents(target);
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    target.focus();
                    button.textContent = 'Text selected';
                    if (status) status.textContent = 'Commands selected. Use your copy shortcut to copy them.';
                    resetLabel();
                };

                if (!navigator.clipboard?.writeText) {
                    selectText();
                    return;
                }

                try {
                    await navigator.clipboard.writeText(target.innerText);
                    button.textContent = 'Copied';
                    if (status) status.textContent = 'Commands copied to the clipboard.';
                    resetLabel();
                } catch {
                    selectText();
                }
            });
        });
    };

    const initializeSkipLinks = () => {
        document.querySelectorAll('a[href="#main-content"]:not([data-ready])').forEach(link => {
            link.dataset.ready = 'true';
            link.addEventListener('click', event => {
                const main = document.getElementById('main-content');
                if (!main) return;

                event.preventDefault();
                main.focus({ preventScroll: true });
                main.scrollIntoView({ block: 'start' });
            });
        });
    };

    const initializeMotion = () => {
        document.querySelectorAll('[data-motion-replay]:not([data-ready])').forEach(button => {
            button.dataset.ready = 'true';
            button.addEventListener('click', () => {
                const scene = button.closest('.scene-wrap')?.querySelector('[data-motion-scene]');
                if (!scene || reducedMotion.matches) return;
                scene.getAnimations({ subtree: true }).forEach(animation => {
                    animation.currentTime = 0;
                    animation.play();
                });
            });
        });

        if (reducedMotion.matches || !('IntersectionObserver' in window)) return;
        const observer = new IntersectionObserver(entries => {
            entries.forEach(entry => {
                if (!entry.isIntersecting) return;
                observer.unobserve(entry.target);
                if (!reducedMotion.matches) entry.target.animate(entrance, entranceTiming);
            });
        }, { threshold: .12 });

        document.querySelectorAll('.section-copy, .code-composition, .toolkit-grid > article, .sample-viewer, .community-panel')
            .forEach(element => {
                if (element.dataset.motionReady) return;
                element.dataset.motionReady = 'true';
                observer.observe(element);
            });
    };

    const updateAmbientMotion = () => {
        document.querySelectorAll('[data-ambient-region]').forEach(region => {
            region.dataset.ambientRunning = String(
                region.dataset.ambientVisible === 'true' && !ambientPaused && !reducedMotion.matches && !document.hidden);
        });
        document.querySelectorAll('[data-ambient-toggle]').forEach(button => {
            button.dataset.paused = String(ambientPaused);
            button.setAttribute('aria-label', ambientPaused ? 'Resume background motion' : 'Pause background motion');
            button.querySelector('[data-ambient-label]').textContent = ambientPaused ? 'Resume motion' : 'Pause motion';
        });
    };

    const initializeAmbientMotion = () => {
        if (!('IntersectionObserver' in window)) return;
        ambientObserver ??= new IntersectionObserver(entries => {
            entries.forEach(entry => { entry.target.dataset.ambientVisible = String(entry.isIntersecting); });
            updateAmbientMotion();
        }, { threshold: .05 });

        document.querySelectorAll('[data-ambient-region]:not([data-ambient-ready])').forEach(region => {
            region.dataset.ambientReady = 'true';
            ambientObserver.observe(region);
        });
        document.querySelectorAll('[data-ambient-toggle]:not([data-ready])').forEach(button => {
            button.dataset.ready = 'true';
            button.hidden = false;
            button.addEventListener('click', () => {
                ambientPaused = !ambientPaused;
                updateAmbientMotion();
            });
        });
        updateAmbientMotion();
    };

    const initialize = () => {
        initializeBoundaryLabs();
        updateTabOrientation();
        initializeCopyButtons();
        initializeSkipLinks();
        initializeMotion();
        initializeAmbientMotion();
    };

    const updateTabOrientation = () => {
        document.querySelectorAll('[data-boundary-lab] [role="tablist"]').forEach(tablist => {
            tablist.setAttribute('aria-orientation', horizontalTabs.matches ? 'horizontal' : 'vertical');
        });
    };

    initialize();
    horizontalTabs.addEventListener('change', updateTabOrientation);
    document.addEventListener('visibilitychange', updateAmbientMotion);
    document.addEventListener('DOMContentLoaded', initialize, { once: true });
    reducedMotion.addEventListener('change', event => {
        if (event.matches) document.getAnimations().forEach(animation => animation.cancel());
        else initializeMotion();
        updateAmbientMotion();
    });

    if (window.Blazor?.addEventListener) {
        window.Blazor.addEventListener('enhancedload', initialize);
    }
})();
