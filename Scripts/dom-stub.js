/* A DOM small enough to run the admin scripts headlessly.

   Not a browser: it exists so the pure decision logic (which panel is shown,
   which renderer a schema selects, whether a preference persists) is testable
   without one. Two blank-render bugs shipped because that logic had no test at
   all while the C# suite stayed green. */

function element(tag = 'div') {
    const node = {
        tagName: String(tag).toUpperCase(),
        children: [],
        attributes: {},
        dataset: {},
        style: {},
        _classes: new Set(),
        _text: '',
        _html: '',
        parentNode: null,
        hidden: false,
        disabled: false,
        value: '',
        checked: false,
        selectionStart: 0,
        classList: {
            add(...c) {
                c.forEach(x => node._classes.add(x));
            },
            remove(...c) {
                c.forEach(x => node._classes.delete(x));
            },
            contains: c => node._classes.has(c),
            toggle(c, force) {
                const on = force === undefined ? !node._classes.has(c) : !!force;
                on ? node._classes.add(c) : node._classes.delete(c);
                return on;
            }
        },
        get className() {
            return [...node._classes].join(' ');
        },
        set className(v) {
            node._classes = new Set(String(v).split(/\s+/).filter(Boolean));
        },
        get innerText() {
            return node._text;
        },
        set innerText(v) {
            node._text = String(v);
        },
        get textContent() {
            return node._text;
        },
        set textContent(v) {
            node._text = String(v);
        },
        get innerHTML() {
            return node._html;
        },
        set innerHTML(v) {
            node._html = String(v);
            node.children = [];
        },
        append(...kids) {
            kids.forEach(k => {
                if (k && typeof k === 'object') {
                    k.parentNode = node;
                    node.children.push(k);
                }
            });
        },
        appendChild(k) {
            node.append(k);
            return k;
        },
        replaceWith() {},
        remove() {
            if (node.parentNode) node.parentNode.children = node.parentNode.children.filter(c => c !== node);
        },
        setAttribute(k, v) {
            node.attributes[k] = String(v);
        },
        getAttribute: k => node.attributes[k],
        removeAttribute(k) {
            delete node.attributes[k];
        },
        hasAttribute: k => k in node.attributes,
        toggleAttribute(k, force) {
            const on = force === undefined ? !(k in node.attributes) : !!force;
            if (on) node.attributes[k] = '';
            else delete node.attributes[k];
            return on;
        },
        addEventListener() {},
        removeEventListener() {},
        focus() {},
        select() {},
        scrollIntoView() {},
        setSelectionRange() {},
        querySelector: () => null,
        querySelectorAll: () => [],
        closest: () => null
    };
    return node;
}

/** Installs globals and returns a registry so a test can assert on elements by id. */
function install(ids = []) {
    const byId = {};
    ids.forEach(id => {
        byId[id] = element();
        byId[id].id = id;
    });

    const store = {};
    global.localStorage = {
        getItem: k => (k in store ? store[k] : null),
        setItem: (k, v) => {
            store[k] = String(v);
        },
        removeItem: k => {
            delete store[k];
        }
    };
    global.document = {
        documentElement: element('html'),
        body: element('body'),
        getElementById: id => byId[id] || null,
        createElement: tag => element(tag),
        querySelector: () => null,
        querySelectorAll: () => [],
        addEventListener() {},
        removeEventListener() {}
    };
    global.window = {
        matchMedia: () => ({
            matches: false,
            addEventListener() {},
            addListener() {}
        }),
        addEventListener() {},
        location: {
            pathname: '/',
            origin: 'http://test'
        }
    };
    global.navigator = {
        language: 'en'
    };
    global.requestAnimationFrame = fn => fn();
    return {
        byId,
        store,
        element
    };
}

module.exports = {
    install,
    element
};