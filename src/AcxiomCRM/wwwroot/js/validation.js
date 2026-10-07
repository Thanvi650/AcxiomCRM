// Client-side adapters for AcxiomCRM's custom server validation attributes.
// They mirror NotInPastAttribute and PositiveAttribute so the browser gives instant
// feedback; the server repeats every check because client rules can be bypassed.
(function ($) {
    'use strict';
    if (!$ || !$.validator || !$.validator.unobtrusive) return;

    // True when the "other" field's value is in the exempt list (e.g. Stage = Won/Lost).
    function isExempt(element, params) {
        if (!params || !params.other) return false;
        var other = $(element.form).find('[name="' + params.other + '"]').val();
        return (params.exempt || '').split(',').indexOf(other) >= 0;
    }

    function parseLocalDate(value) {
        // "yyyy-MM-dd" or "yyyy-MM-ddTHH:mm" from date / datetime-local inputs.
        var match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value || '');
        if (!match) return null;
        return new Date(+match[1], +match[2] - 1, +match[3]);
    }

    $.validator.addMethod('notinpast', function (value, element, params) {
        if (this.optional(element) || isExempt(element, params)) return true;
        var date = parseLocalDate(value);
        if (!date) return true;
        var today = new Date();
        today.setHours(0, 0, 0, 0);
        return date >= today;
    });

    $.validator.unobtrusive.adapters.add('notinpast', ['other', 'exempt'], function (options) {
        options.rules.notinpast = { other: options.params.other, exempt: options.params.exempt };
        options.messages.notinpast = options.message;
    });

    $.validator.addMethod('positive', function (value, element, params) {
        if (this.optional(element) || isExempt(element, params)) return true;
        var number = parseFloat(value);
        return !isNaN(number) && number > 0;
    });

    $.validator.unobtrusive.adapters.add('positive', ['other', 'exempt'], function (options) {
        options.rules.positive = { other: options.params.other, exempt: options.params.exempt };
        options.messages.positive = options.message;
    });

    // Numeric precision, e.g. money with at most 2 decimal places.
    $.validator.addMethod('decimalplaces', function (value, element, places) {
        if (this.optional(element)) return true;
        return new RegExp('^-?\\d*(\\.\\d{0,' + places + '})?$').test($.trim(value));
    });

    $.validator.unobtrusive.adapters.addSingleVal('decimalplaces', 'places');

    // Required only while another field has one of the given values (e.g. Stage = Lost).
    $.validator.addMethod('requiredwhen', function (value, element, params) {
        var other = $(element.form).find('[name="' + params.other + '"]').val();
        var applies = (params.values || '').split(',').indexOf(other) >= 0;
        return !applies || $.trim(value).length > 0;
    });

    $.validator.unobtrusive.adapters.add('requiredwhen', ['other', 'values'], function (options) {
        options.rules.requiredwhen = { other: options.params.other, values: options.params.values };
        options.messages.requiredwhen = options.message;
    });

    // Re-check dependent fields when the controlling field (e.g. Stage) changes.
    $(document).on('change', 'select', function () {
        var form = $(this.form);
        if (!form.data('validator')) return;
        form.find('[data-val-notinpast-other="' + this.name + '"], [data-val-positive-other="' + this.name + '"], [data-val-requiredwhen-other="' + this.name + '"]').each(function () {
            if ($(this).val() || this.hasAttribute('data-val-requiredwhen')) form.validate().element(this);
        });
    });

    // Bootstrap styling for invalid fields.
    $.validator.setDefaults({
        highlight: function (element) { $(element).addClass('is-invalid input-validation-error'); },
        unhighlight: function (element) { $(element).removeClass('is-invalid input-validation-error'); }
    });
})(window.jQuery);
