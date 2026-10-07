// Dashboard charts (Chart.js). Data is rendered server-side from the user's authorized scope.
(function () {
    'use strict';

    // Date-range toggle: presets submit immediately, "Custom" reveals the date inputs.
    var rangeForm = document.getElementById('rangeForm');
    if (rangeForm) {
        rangeForm.querySelectorAll('input[name="Range"]').forEach(function (radio) {
            radio.addEventListener('change', function () {
                var custom = document.getElementById('customRange');
                if (radio.value === 'custom') {
                    custom.classList.remove('d-none');
                } else {
                    custom.querySelectorAll('input').forEach(function (i) { i.value = ''; });
                    rangeForm.submit();
                }
            });
        });
    }

    var dataEl = document.getElementById('chartData');
    if (!dataEl || !window.Chart) return;
    var data = JSON.parse(dataEl.textContent);

    var rupee = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 });
    var compact = new Intl.NumberFormat('en-IN', { notation: 'compact', maximumFractionDigits: 1 });

    Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
    Chart.defaults.color = '#627d98';
    Chart.defaults.plugins.legend.labels.usePointStyle = true;

    var palette = {
        New: '#3b82f6', Contacted: '#06b6d4', Qualified: '#22c55e', Unqualified: '#94a3b8', Lost: '#ef4444', Converted: '#334155',
        Qualification: '#60a5fa', Proposal: '#3b82f6', Negotiation: '#f59e0b', Won: '#16a34a'
    };
    var colorFor = function (label) { return palette[label] || '#94a3b8'; };

    new Chart(document.getElementById('leadStatusChart'), {
        type: 'doughnut',
        data: {
            labels: data.leadStatus.labels,
            datasets: [{ data: data.leadStatus.values, backgroundColor: data.leadStatus.labels.map(colorFor), borderWidth: 2, borderColor: '#fff' }]
        },
        options: {
            maintainAspectRatio: false,
            cutout: '62%',
            plugins: { legend: { position: 'bottom' } }
        }
    });

    new Chart(document.getElementById('pipelineChart'), {
        type: 'bar',
        data: {
            labels: data.pipeline.labels,
            datasets: [
                {
                    label: 'Amount (₹)', data: data.pipeline.amounts, yAxisID: 'y',
                    backgroundColor: data.pipeline.labels.map(colorFor), borderRadius: 6, maxBarThickness: 56
                },
                {
                    label: 'Deals', data: data.pipeline.counts, yAxisID: 'y1', type: 'line',
                    borderColor: '#102a43', backgroundColor: '#102a43', cubicInterpolationMode: 'monotone', pointRadius: 4
                }
            ]
        },
        options: {
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            scales: {
                y: { beginAtZero: true, ticks: { callback: function (v) { return '₹' + compact.format(v); } }, grid: { color: '#eef2f7' } },
                y1: { beginAtZero: true, position: 'right', grid: { display: false }, ticks: { precision: 0 } },
                x: { grid: { display: false } }
            },
            plugins: {
                tooltip: {
                    callbacks: {
                        label: function (ctx) {
                            return ctx.dataset.yAxisID === 'y' ? ' Amount: ' + rupee.format(ctx.parsed.y) : ' Deals: ' + ctx.parsed.y;
                        }
                    }
                }
            }
        }
    });

    new Chart(document.getElementById('monthlyChart'), {
        type: 'bar',
        data: {
            labels: data.monthly.labels,
            datasets: [
                { label: 'Won revenue (₹)', data: data.monthly.amounts, backgroundColor: '#2563eb', borderRadius: 6, maxBarThickness: 40, yAxisID: 'y' },
                { label: 'Deals won', data: data.monthly.deals, type: 'line', borderColor: '#16a34a', backgroundColor: '#16a34a', cubicInterpolationMode: 'monotone', yAxisID: 'y1' }
            ]
        },
        options: {
            maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            scales: {
                y: { beginAtZero: true, ticks: { callback: function (v) { return '₹' + compact.format(v); } }, grid: { color: '#eef2f7' } },
                y1: { beginAtZero: true, position: 'right', grid: { display: false }, ticks: { precision: 0 } },
                x: { grid: { display: false } }
            },
            plugins: {
                tooltip: {
                    callbacks: {
                        label: function (ctx) {
                            return ctx.dataset.yAxisID === 'y' ? ' Revenue: ' + rupee.format(ctx.parsed.y) : ' Deals: ' + ctx.parsed.y;
                        }
                    }
                }
            }
        }
    });
})();
