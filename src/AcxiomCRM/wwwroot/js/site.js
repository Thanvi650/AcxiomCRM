// AcxiomCRM site-wide behaviour.
(function () {
    'use strict';

    // Confirmation dialog for destructive / irreversible actions:
    // <form data-confirm="Delete this customer?" data-confirm-button="Delete"> ... </form>
    var modalEl = document.getElementById('confirmModal');
    if (modalEl && window.bootstrap) {
        var modal = new bootstrap.Modal(modalEl);
        var pendingForm = null;

        document.addEventListener('submit', function (event) {
            var form = event.target;
            if (!(form instanceof HTMLFormElement) || !form.dataset.confirm || form.dataset.confirmed === 'true') {
                return;
            }
            event.preventDefault();
            pendingForm = form;
            document.getElementById('confirmModalBody').textContent = form.dataset.confirm;
            var ok = document.getElementById('confirmModalOk');
            ok.textContent = form.dataset.confirmButton || 'Confirm';
            ok.className = 'btn ' + (form.dataset.confirmStyle || 'btn-danger');
            modal.show();
        }, true);

        document.getElementById('confirmModalOk').addEventListener('click', function () {
            if (!pendingForm) return;
            pendingForm.dataset.confirmed = 'true';
            modal.hide();
            if (typeof pendingForm.requestSubmit === 'function') pendingForm.requestSubmit();
            else pendingForm.submit();
        });
    }

    // Auto-submit filter forms when a select changes.
    document.querySelectorAll('form[data-autosubmit] select').forEach(function (select) {
        select.addEventListener('change', function () { select.form.submit(); });
    });

    // Auto-hide success alerts.
    setTimeout(function () {
        document.querySelectorAll('.alert-success.alert-dismissible').forEach(function (el) {
            if (window.bootstrap) bootstrap.Alert.getOrCreateInstance(el).close();
        });
    }, 6000);
})();
